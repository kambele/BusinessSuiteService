Imports System.Configuration
Imports System.Data.SqlClient
Imports System.Globalization
Imports System.IO
Imports System.Text

Friend Enum WorkflowLogLevel
    Debug
    Info
    Warning
    [Error]
    Off
End Enum

Friend NotInheritable Class ServiceLog
    Private Const FilePrefix As String = "BusinessSuiteWorkflow-"
    Private Const MessageLimit As Integer = 4096
    Private Shared ReadOnly FileEncoding As New UTF8Encoding(False)
    Private Shared ReadOnly RepeatInterval As TimeSpan = TimeSpan.FromMinutes(5)
    Private ReadOnly writeLock As New Object()
    Private ReadOnly directory As String
    Private ReadOnly minimumLevel As WorkflowLogLevel
    Private ReadOnly maxBytes As Long
    Private ReadOnly getNow As Func(Of DateTime)
    Private ReadOnly repeats As New Dictionary(Of String, RepeatState)(StringComparer.Ordinal)
    Private writer As StreamWriter
    Private logDate As Date = Date.MinValue
    Private part As Integer
    Private length As Long

    Private Class RepeatState
        Public Property LastWritten As DateTime
        Public Property Suppressed As Integer
    End Class

    Friend Sub New(ByVal logDirectory As String, ByVal level As WorkflowLogLevel, ByVal fileSizeLimit As Long, ByVal clock As Func(Of DateTime))
        directory = logDirectory
        minimumLevel = level
        maxBytes = fileSizeLimit
        getNow = clock
    End Sub

    Friend Shared Function FromSettings() As ServiceLog
        ' Legacy Debug=1 enabled unbounded trace output. Verbose output now requires LogLevel=Debug.
        Dim level As WorkflowLogLevel
        If Not [Enum].TryParse(ConfigurationManager.AppSettings("LogLevel"), True, level) OrElse
           Not [Enum].IsDefined(GetType(WorkflowLogLevel), level) Then level = WorkflowLogLevel.Info
        Dim sizeMb As Integer
        If Not Integer.TryParse(ConfigurationManager.AppSettings("LogMaxFileSizeMB"), sizeMb) OrElse sizeMb < 1 Then sizeMb = 10
        Return New ServiceLog("C:\Debug", level, CLng(sizeMb) * 1024 * 1024, Function() DateTime.Now)
    End Function

    Friend Sub Write(ByVal level As WorkflowLogLevel, ByVal message As String, Optional ByVal repeatKey As String = Nothing)
        If level < minimumLevel OrElse level = WorkflowLogLevel.Off OrElse String.IsNullOrWhiteSpace(message) Then Return

        Try
            SyncLock writeLock
                Dim now = getNow()
                Dim state As RepeatState = Nothing
                If repeatKey IsNot Nothing Then
                    repeatKey = level.ToString() & ":" & repeatKey
                    If repeats.TryGetValue(repeatKey, state) AndAlso now >= state.LastWritten AndAlso now - state.LastWritten < RepeatInterval Then
                        state.Suppressed += 1
                        Return
                    End If
                End If

                Dim line = now.ToString("yyyy-MM-dd HH:mm:ss.fff", CultureInfo.InvariantCulture) & " [" & level.ToString().ToUpperInvariant() & "] " & OneLine(message)
                If state IsNot Nothing AndAlso state.Suppressed > 0 Then
                    line &= " (" & state.Suppressed.ToString(CultureInfo.InvariantCulture) & " repeats suppressed)"
                End If
                Dim bytes = FileEncoding.GetByteCount(line) + FileEncoding.GetByteCount(Environment.NewLine)
                EnsureWriter(now.Date, bytes)
                writer.WriteLine(line)
                length += bytes
                If repeatKey IsNot Nothing Then repeats(repeatKey) = New RepeatState With {.LastWritten = now}
            End SyncLock
        Catch
            ' Logging errors must not interrupt an assignment or workflow cycle.
            Close()
        End Try
    End Sub

    Friend Sub Failure(ByVal operation As String, ByVal ex As Exception, Optional ByVal context As String = Nothing)
        If minimumLevel > WorkflowLogLevel.Error Then Return
        Dim cause = ex.GetBaseException()
        Dim sqlError = TryCast(cause, SqlException)
        Dim code = If(sqlError Is Nothing, cause.HResult, sqlError.Number)
        Dim details = cause.GetType().Name & " (" & code.ToString(CultureInfo.InvariantCulture) & "): " & cause.Message
        If minimumLevel = WorkflowLogLevel.Debug AndAlso Not String.IsNullOrWhiteSpace(cause.StackTrace) Then details &= " | " & cause.StackTrace
        Write(WorkflowLogLevel.Error, operation & If(String.IsNullOrWhiteSpace(context), "", " (" & context & ")") & ": " & details,
              operation & ":" & cause.GetType().FullName & ":" & code.ToString(CultureInfo.InvariantCulture))
    End Sub

    Friend Sub Close()
        SyncLock writeLock
            Try
                CloseWriter()
            Catch
                ' Disposal errors must not prevent the service from stopping.
            End Try
        End SyncLock
    End Sub

    Private Sub EnsureWriter(ByVal day As Date, ByVal recordBytes As Integer)
        If writer IsNot Nothing AndAlso logDate = day AndAlso (length = 0 OrElse length + recordBytes <= maxBytes) Then Return

        Dim sameDay = writer IsNot Nothing AndAlso logDate = day
        CloseWriter()
        IO.Directory.CreateDirectory(directory)
        Dim stem = FilePrefix & day.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture)
        If sameDay Then
            part += 1
        Else
            part = 0
            For Each existing In IO.Directory.EnumerateFiles(directory, stem & ".*.txt")
                Dim name = Path.GetFileNameWithoutExtension(existing)
                Dim number As Integer
                If name.StartsWith(stem & ".", StringComparison.Ordinal) AndAlso
                   Integer.TryParse(name.Substring(stem.Length + 1), NumberStyles.None, CultureInfo.InvariantCulture, number) Then part = Math.Max(part, number)
            Next
        End If
        Dim pathName As String
        Do
            pathName = Path.Combine(directory, stem & If(part = 0, "", "." & part.ToString(CultureInfo.InvariantCulture)) & ".txt")
            Dim file As New FileInfo(pathName)
            length = If(file.Exists, file.Length, 0)
            If length = 0 OrElse length + recordBytes <= maxBytes Then Exit Do
            part += 1
        Loop

        writer = New StreamWriter(pathName, True, FileEncoding) With {.AutoFlush = True}
        logDate = day
    End Sub

    Private Sub CloseWriter()
        If writer Is Nothing Then Return
        Dim openWriter = writer
        writer = Nothing
        openWriter.Dispose()
    End Sub

    Private Shared Function OneLine(ByVal value As String) As String
        If value.Length > MessageLimit Then value = value.Substring(0, MessageLimit) & "..."
        Return value.Replace(vbCrLf, " | ").Replace(vbCr, " ").Replace(vbLf, " ")
    End Function
End Class
