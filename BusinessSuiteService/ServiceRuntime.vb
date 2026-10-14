Imports System.Configuration
Imports System.Data
Imports System.Data.SqlClient

Friend NotInheritable Class ServiceRuntime
    Private Const RunLockName As String = "BusinessSuiteService.WorkflowCycle"

    Private Sub New()
    End Sub

    Public Shared ReadOnly Property PollInterval As TimeSpan
        Get
            Return TimeSpan.FromMinutes(GetPositiveInteger("SleepInMinutes", 2))
        End Get
    End Property

    Public Shared ReadOnly Property StopWait As TimeSpan
        Get
            Return TimeSpan.FromSeconds(GetPositiveInteger("StopWaitSeconds", 30))
        End Get
    End Property

    Public Shared ReadOnly Property AutoAssignBatchSize As Integer
        Get
            Return GetPositiveInteger("AutoAssignBatchSize", 250)
        End Get
    End Property

    Public Shared ReadOnly Property StatusChangeBatchSize As Integer
        Get
            Return GetPositiveInteger("StatusChangeBatchSize", 250)
        End Get
    End Property

    Public Shared ReadOnly Property CommandTimeoutSeconds As Integer
        Get
            Return GetPositiveInteger("DatabaseCommandTimeoutSeconds", 120)
        End Get
    End Property

    Public Shared ReadOnly Property PostTimeoutMilliseconds As Integer
        Get
            Return checkedSecondsToMilliseconds(GetPositiveInteger("PostTimeoutSeconds", 60))
        End Get
    End Property

    Public Shared Function IsEnabled(key As String, Optional defaultValue As Boolean = False) As Boolean
        Dim configuredValue = ConfigurationManager.AppSettings.Get(key)
        If String.IsNullOrWhiteSpace(configuredValue) Then
            Return defaultValue
        End If

        Select Case configuredValue.Trim().ToUpperInvariant()
            Case "1", "TRUE", "YES", "ON"
                Return True
            Case "0", "FALSE", "NO", "OFF"
                Return False
            Case Else
                Return defaultValue
        End Select
    End Function

    Public Shared Function TryAcquireRunLock() As SqlConnection
        Dim connection As New SqlConnection(ConfigurationManager.AppSettings.Get("DB"))
        connection.Open()

        Using command As New SqlCommand(
            "DECLARE @result int; " &
            "EXEC @result = sys.sp_getapplock " &
            "@Resource = @resource, @LockMode = 'Exclusive', @LockOwner = 'Session', " &
            "@LockTimeout = 0, @DbPrincipal = 'public'; " &
            "SELECT @result;", connection)
            command.CommandTimeout = CommandTimeoutSeconds
            command.Parameters.Add("@resource", SqlDbType.NVarChar, 255).Value = RunLockName

            Dim result = Convert.ToInt32(command.ExecuteScalar())
            If result < 0 Then
                connection.Dispose()
                Return Nothing
            End If
        End Using

        Return connection
    End Function

    Public Shared Function SynchronizeLoadingDates(connection As SqlConnection) As Integer
        Const sql As String =
";WITH first_tasks AS
(
    SELECT task_row.id,
           CASE
               WHEN UPPER(LTRIM(RTRIM(ISNULL(definition.loading_type, 'INSTANT')))) = 'AFTER DUE DATE'
                    AND process_row.form_due_date IS NOT NULL
                   THEN dbo.DateAddWorkDay(1, process_row.form_due_date)
               ELSE COALESCE(process_row.form_action_date,
                             process_row.submitted_on,
                             process_row.created_on,
                             process_row.date_created)
           END AS loading_date
    FROM dbo.tasks task_row
    INNER JOIN dbo.processes process_row ON process_row.id = task_row.process
    INNER JOIN dbo.process_definitions definition ON definition.id = process_row.process_definition
    LEFT JOIN dbo.task_definitions task_definition ON task_definition.id = task_row.task_definition
    WHERE task_row.assigned_to IS NULL
      AND task_row.completion_date IS NULL
      AND ISNULL(task_row.status, 1) = 1
      AND ISNULL(task_row.is_deleted, 0) = 0
      AND ISNULL(task_row.sort_order, task_definition.sort_order) = 1
)
UPDATE task_row
SET created_on = first_tasks.loading_date,
    date_updated = GETDATE()
FROM dbo.tasks task_row
INNER JOIN first_tasks ON first_tasks.id = task_row.id
WHERE first_tasks.loading_date IS NOT NULL
  AND (task_row.created_on IS NULL OR task_row.created_on <> first_tasks.loading_date);"

        Using command As New SqlCommand(sql, connection)
            command.CommandTimeout = CommandTimeoutSeconds
            Return command.ExecuteNonQuery()
        End Using
    End Function

    Public Shared Function UpdateMissingTaskSortOrders(connection As SqlConnection) As Integer
        Const sql As String =
";WITH task_order AS
(
    SELECT task_row.id,
           task_definition.sort_order AS definition_sort_order,
           ROW_NUMBER() OVER
           (
               PARTITION BY task_row.process
               ORDER BY CASE WHEN task_definition.sort_order IS NULL THEN 1 ELSE 0 END,
                        task_definition.sort_order,
                        task_row.id
           ) AS calculated_sort_order
    FROM dbo.tasks task_row
    LEFT JOIN dbo.task_definitions task_definition ON task_definition.id = task_row.task_definition
)
UPDATE task_row
SET sort_order = COALESCE(task_order.definition_sort_order, task_order.calculated_sort_order),
    date_updated = GETDATE()
FROM dbo.tasks task_row
INNER JOIN task_order ON task_order.id = task_row.id
WHERE task_row.sort_order IS NULL;"

        Using command As New SqlCommand(sql, connection)
            command.CommandTimeout = CommandTimeoutSeconds
            Return command.ExecuteNonQuery()
        End Using
    End Function

    Public Shared Function GetEligibleTaskIds() As List(Of Long)
        Dim taskIds As New List(Of Long)()

        Using connection As New SqlConnection(ConfigurationManager.AppSettings.Get("DB"))
            connection.Open()

            Using command As New SqlCommand("dbo.GetUnassignedTaskList", connection)
                command.CommandType = CommandType.StoredProcedure
                command.CommandTimeout = CommandTimeoutSeconds
                command.Parameters.Add("@BatchSize", SqlDbType.Int).Value = AutoAssignBatchSize

                Using reader = command.ExecuteReader(CommandBehavior.SequentialAccess)
                    While reader.Read()
                        If Not reader.IsDBNull(0) Then
                            taskIds.Add(reader.GetInt64(0))
                        End If
                    End While
                End Using
            End Using
        End Using

        Return taskIds.Distinct().ToList()
    End Function

    Public Shared Function GetPendingStatusChangeProcessIds(connection As SqlConnection) As List(Of Long)
        Dim processIds As New List(Of Long)()
        Const sql As String =
"IF COL_LENGTH('dbo.processes', 'form_status_change') IS NULL
BEGIN
    SELECT CAST(NULL AS bigint) AS id WHERE 1 = 0;
    RETURN;
END;

SELECT TOP (@BatchSize) process_row.id
FROM dbo.processes AS process_row WITH (READPAST)
WHERE process_row.form_status_change = 1
  AND ISNULL(process_row.is_deleted, 0) = 0
ORDER BY COALESCE(process_row.date_updated, process_row.date_created), process_row.id;"

        Using command As New SqlCommand(sql, connection)
            command.CommandTimeout = CommandTimeoutSeconds
            command.Parameters.Add("@BatchSize", SqlDbType.Int).Value = StatusChangeBatchSize

            Using reader = command.ExecuteReader(CommandBehavior.SequentialAccess)
                While reader.Read()
                    If Not reader.IsDBNull(0) Then
                        processIds.Add(reader.GetInt64(0))
                    End If
                End While
            End Using
        End Using

        Return processIds
    End Function

    Public Shared Function IsStatusChangePending(connection As SqlConnection, processId As Long) As Boolean
        Const sql As String =
"SELECT CASE WHEN EXISTS
(
    SELECT 1
    FROM dbo.processes
    WHERE id = @ProcessId
      AND form_status_change = 1
) THEN 1 ELSE 0 END;"

        Using command As New SqlCommand(sql, connection)
            command.CommandTimeout = CommandTimeoutSeconds
            command.Parameters.Add("@ProcessId", SqlDbType.BigInt).Value = processId
            Return Convert.ToInt32(command.ExecuteScalar()) = 1
        End Using
    End Function

    Private Shared Function GetPositiveInteger(key As String, defaultValue As Integer) As Integer
        Dim parsedValue As Integer
        If Integer.TryParse(ConfigurationManager.AppSettings.Get(key), parsedValue) AndAlso parsedValue > 0 Then
            Return parsedValue
        End If

        Return defaultValue
    End Function

    Private Shared Function checkedSecondsToMilliseconds(seconds As Integer) As Integer
        If seconds > Integer.MaxValue \ 1000 Then
            Return Integer.MaxValue
        End If

        Return seconds * 1000
    End Function
End Class
