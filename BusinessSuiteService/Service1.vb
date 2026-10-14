Imports System.Configuration
Imports System.Data.Entity.Core.Metadata.Edm
Imports System.Data.SqlClient
Imports System.Globalization
Imports System.Net
Imports System.Net.Http
Imports System.Runtime.Remoting.Messaging
Imports System.Text
Imports System.Text.Json
Imports System.Threading
Imports System.Diagnostics
Imports Newtonsoft.Json

Public Class Service1

    Public db As BusinessSuiteEntities
    Public vsab As VSABEntities
    Public rs As ReportingSuiteEntities

    Public t2 As Thread
    Private ReadOnly stopSignal As New ManualResetEventSlim(False)
    Private Shared ReadOnly logSync As New Object()
    Private Shared logWriter As IO.StreamWriter
    Private Shared logWriterDate As Date = Date.MinValue

    Private Const ConnString As String =
        "Server=YOUR_SERVER;Database=BusinessSuite;Trusted_Connection=True;TrustServerCertificate=True;"

    ' Limit to your enum if you don’t want every currency
    Private ReadOnly KeepSymbols As HashSet(Of String) =
        New HashSet(Of String)(StringComparer.OrdinalIgnoreCase) From {
            "USD", "EUR", "GBP", "NAD", "ZAR", "CNY", "JPY", "AUD"
        }
    Protected Overrides Sub OnStart(ByVal args() As String)
        stopSignal.Reset()
        t2 = New Thread(AddressOf ExecuteAutoAssign)
        t2.IsBackground = True
        t2.Name = "BusinessSuite workflow worker"
        t2.Start()
    End Sub

    Public Sub CheckAnnualCancellations()
        log("Starting Executing: CheckAnnualCancellations")
        For Each entity In db.entities.Where(Function(f) f.expiry_flag = "Queued for Cancallation" And f.is_deleted = False)
            log(">> Entity Found: Queued for Cancallation")
            log("")
            log("Entity Name: " & entity.entity_name & "(License: " & entity.license_number & ") Expiry Date: " & entity.license_expriy_date)

            Try
                Dim forms As New List(Of FormValue)


                forms.Add(New FormValue("expiry_flag", "Cancelled"))
                Dim m = db.modules.Single(Function(f) f.database_table = "entities")
                Dim p = m.module_pages.First(Function(f) f.name = "Cancel Entity")
                forms.Add(New FormValue("page_id", p.id))
                forms.Add(New FormValue("data_id", entity.id))
                forms.Add(New FormValue("prefix", ""))
                forms.Add(New FormValue("return_url", ""))
                Dim vs As New List(Of String)


                Dim param = New Dictionary(Of String, String)
                For Each v In forms
                    param.Add(v.Name, v.Value)
                Next

                log(JsonConvert.SerializeObject(param))
                log("")
                log("")
                SubmitForm(param)
            Catch ex As Exception
                log("      ---- Error: ")
                log("           >> To be cancelled: ")
                log("                  Entity Name: " & entity.entity_name & "(License: " & entity.license_number & "")
                log("      ---- Error: " & ex.Message)
            End Try

        Next
        log("Finished Executing: CheckAnnualCancellations")
    End Sub

    Public Sub CheckReturnApplicationExceeding7days()
        Dim startFrom = Date.Parse("2025-07-14")

        If CheckPastOrFuture(startFrom) = "past" Then
            Dim checkStartFrom As Date = Date.Parse("2025-07-14")
            Dim returnedTasks = db.processes.Where(Function(f) (f.statuss = 2 Or f.statuss = 8) And (f.external_status = "No Data" Or f.external_status = "Valid" Or f.external_status = "In Draft") And f.submitted_on > checkStartFrom).ToList()
            For Each r In returnedTasks


                Try
                    Dim process = db.processes.Single(Function(f) f.id = r.id)

                    Dim spi = vsab.submission_pack_instances.Single(Function(f) f.reference = process.external_reference)
                    Dim spv = vsab.submission_pack_versions.Where(Function(f) f.submission_pack_instance_id = spi.submission_pack_instance_id).OrderByDescending(Function(f) f.submission_pack_version_id).First

                    vsab.Entry(spv).Reload()

                    Dim dateReturn = spv.created_on
                    Dim today = Date.Now

                    Dim lastDate = AddWeekdays(dateReturn, 7)



                    Dim activeTaskList = db.tasks.Where(Function(f) f.process = r.id)

                    Dim activeTask = activeTaskList.Where(Function(f) f.status = 2 Or f.status = 8).OrderByDescending(Function(f) f.sort_order).First

                    If Not activeTask Is Nothing Then
                        Try
                            If CheckPastOrFuture(lastDate) = "past" Then
                                log(">> Returned Process: Check If 7 days exceeded: " & r.external_reference)
                                log(">> Return Process: " & r.name & " ( " & process.name & " : " & process.external_reference & " ) ")
                                log("Date Created: " & dateReturn)
                                log("Last Date of Submission: " & lastDate)
                                log("   >> Returning Task Found: " & activeTask.name)
                                log(">> Returned Process: 7 days exceeded")
                                Dim forms As New List(Of FormValue)

                                forms.Add(New FormValue("status", "11"))

                                PostForm(forms, db.module_pages.Single(Function(f) f.name = "7 Days Period Exceeded"), activeTask.id)
                            Else
                                log(">> Returned Process: 7 days not exceeded")
                            End If



                        Catch ex As Exception
                            log("      ---- Error: ")
                            log("           >> Unable to Close 7 Days: ")
                            log("      ---- Error: " & ex.Message)
                            log("      ---- Error: " & ex.StackTrace)
                        End Try
                    Else
                        log("      ---- Error: Active Task Not Found: " & process.name)
                    End If




                Catch ex As Exception
                    log("      ---- Error: ")
                    log("           >> Unable to Close 7 Days: " & r.external_reference)
                    log("      ---- Error: " & ex.Message)
                    log("      ---- Error: " & ex.StackTrace)
                End Try

            Next

        End If


    End Sub
    Function CheckPastOrFuture(ByVal inputDate As DateTime) As String
        Dim currentDate As DateTime = DateTime.Now

        If inputDate < currentDate Then
            Return "past"
        ElseIf inputDate > currentDate Then
            Return "future"
        Else
            Return "present"
        End If
    End Function
    Public Sub CheckResubmissionExecute()

        Dim returnedProcesses = db.processes.Where(Function(f) (f.statuss = 8 Or f.statuss = 11) And f.external_status = "Pending Approval")
        For Each rp In returnedProcesses
            Try
                Dim executed = False
                Dim process = db.processes.Single(Function(f) f.id = rp.id)
                log(">> Return Tasks:   ( " & process.name & " : " & process.external_reference & " ) ")
                Dim returnedTasks = db.tasks.Where(Function(f) f.status = rp.statuss And f.process = rp.id).ToList()
                If Not returnedTasks Is Nothing Then
                    If returnedTasks.Count = 1 Then
                        For Each r In returnedTasks
                            Dim forms As New List(Of FormValue)

                            forms.Add(New FormValue("status", "9"))

                            PostForm(forms, db.module_pages.Single(Function(f) f.name = "Complete 7 Days Letter"), r.id)
                            executed = True
                            log("   ||> Assigned to 7 Days Letter Task")
                        Next
                    End If
                End If

                ''Get Task which is open
                If executed = False Then
                    returnedTasks = db.tasks.Where(Function(f) (f.status = 2 Or f.status = 7) And f.process = rp.id).ToList()
                    If Not returnedTasks Is Nothing Then
                        If returnedTasks.Count = 1 Then
                            For Each r In returnedTasks
                                Dim forms As New List(Of FormValue)

                                forms.Add(New FormValue("status", "9"))

                                PostForm(forms, db.module_pages.Single(Function(f) f.name = "Complete 7 Days Letter"), r.id)
                                executed = True
                                log("   ||> Assigned to Open Task")
                            Next
                        End If
                    End If
                End If

                If Not executed Then
                    log(" Error: Error   ||> No Task to open")
                End If
            Catch ex As Exception
                log("      ---- Error: ")
                log("           >> Unable to Close 7 Days: ")
                log("      ---- Error: " & ex.Message)
            End Try


        Next




    End Sub

    Function AddWeekdays(ByVal startDate As DateTime, ByVal daysToAdd As Integer) As DateTime
        Dim resultDate As DateTime = startDate

        For i As Integer = 1 To daysToAdd
            resultDate = resultDate.AddDays(1)

            ' Check if the current date is a Saturday or Sunday
            If resultDate.DayOfWeek = DayOfWeek.Saturday OrElse resultDate.DayOfWeek = DayOfWeek.Sunday Then
                ' If it's a weekend day, skip it by adding an extra day
                resultDate = resultDate.AddDays(1)
            End If
        Next

        Return resultDate
    End Function
    Public Sub CheckReturnedExecute()
        Dim returnedTasks = db.processes.Where(Function(f) f.statuss = 2 And (f.external_status = "No Data" Or f.external_status = "Valid" Or f.external_status = "In Draft")).ToList()
        For Each r In returnedTasks
            log(">> Return Process: Initiate 7 Days Period")

            Try
                Dim process = db.processes.Single(Function(f) f.id = r.id)
                log(">> Return Process: " & r.name & " ( " & process.name & " : " & process.external_reference & " ) ")
                Dim spi = vsab.submission_pack_instances.SingleOrDefault(Function(f) f.reference = process.external_reference)
                Dim spv = vsab.submission_pack_versions.Where(Function(f) f.submission_pack_instance_id = spi.submission_pack_instance_id).OrderByDescending(Function(f) f.submission_pack_version_id).First

                Dim activeTaskList = db.tasks.Where(Function(f) f.process = r.id)
                Dim activeTask = activeTaskList.Where(Function(f) f.status = 2 Or f.status = 7 Or f.status = 8).OrderByDescending(Function(f) f.sort_order).First
                Dim executed = False
                If Not activeTask Is Nothing Then
                    Try
                        Dim forms As New List(Of FormValue)

                        forms.Add(New FormValue("status", "8"))

                        PostForm(forms, db.module_pages.Find(40992), activeTask.id)
                        executed = True
                    Catch ex As Exception
                        log("      ---- Error: ")
                        log("           >> Unable to Close 7 Days: ")
                        log("      ---- Error: " & ex.Message)
                        log("      ---- Error: " & ex.StackTrace)
                    End Try
                End If

                If executed = False Then

                End If



            Catch ex As Exception
                log("      ---- Error: ")
                log("           >> Unable to Close 7 Days: ")
                log("      ---- Error: " & ex.Message)
                log("      ---- Error: " & ex.StackTrace)
            End Try

        Next


    End Sub
    Public Sub CheckResubmission()

        Do
            Try
                CheckResubmissionExecute()
                ''Thread.Sleep(60000 * CInt(ConfigurationManager.AppSettings.Get("SleepInMinutes")))
            Catch ex As Exception
                log("      ---- Error: ")
                log("           >> Unable to Close 7 Days: ")
                log("      ---- Error: " & ex.Message)
            End Try

        Loop


    End Sub

    Public Sub UpdateProcessSLC()
        'Dim m = db.modules.Single(Function(f) f.slug = "processes")
        'For Each p In db.processes.Where(Function(f) f.statuss <> 4)
        '    autoCalc(m, p, p.id, db)
        'Next
        'db.SaveChanges()
        'For Each p In db.processes.Where(Function(f) f.statuss <> 4)
        '    p.days_spent = db.tasks.Where(Function(f) f.process = p.id).Sum(Function(f) f.days_spent)
        'Next
        'db.SaveChanges()
    End Sub
    'Public Sub ExecuteLoadingSingle(ByVal reference As String)
    '    Try
    '        For Each td In db.process_types.Where(Function(f) f.is_deleted = False And Not f.form_type Is Nothing And Not f.process_definition Is Nothing And Not f.entity_group Is Nothing And f.entity_group <> 0)
    '            log("           >> Checking: " & td.name)

    '            ''Update Submission packs
    '            Try
    '                Dim form = db.form_types.Single(Function(f) f.id = td.form_type)
    '                log("           >>  Form: " & form.form_name)
    '                Dim entity_type = db.entity_types.Where(Function(f) f.id = td.entity_group).Single
    '                Dim packs = db.GetBusinessSuiteSubmissionPacksSingle(form.form_type_id, entity_type.entity_group_id, reference).ToList

    '                For Each s In packs
    '                    log("           >> Found: " & s.reference & " - " & s.submission_pack_name)

    '                    If db.processes.Where(Function(f) f.external_reference = s.reference And f.is_deleted = False).Count = 0 Then
    '                        log("           >> To be added: " & s.reference & " - " & s.submission_pack_name)

    '                        Dim m = db.modules.Find(10017)
    '                        Dim page = db.module_pages.Find(40596)
    '                        log("")
    '                        log("")
    '                        For Each i In page.module_page_items.Where(Function(f) f.is_show)
    '                            log(" - " & i.module_items.database_column)
    '                        Next
    '                        Try
    '                            Dim forms As New List(Of FormValue)

    '                            Dim entity = db.entities.Where(Function(f) f.entity_instance_id = s.entity_instance_id).Single


    '                            forms.Add(New FormValue("process_definition", td.process_definition))
    '                            forms.Add(New FormValue("activity_type", td.id))
    '                            forms.Add(New FormValue("division", td.division))
    '                            forms.Add(New FormValue("entity", entity.id))
    '                            forms.Add(New FormValue("entity_type", entity_type.id))
    '                            forms.Add(New FormValue("external_reference", s.reference))
    '                            forms.Add(New FormValue("created_on", s.created_on.Value.ToString("yyyy-MM-dd HH:mm:ss")))
    '                            forms.Add(New FormValue("submitted_on", s.submitted_on.Value.ToString("yyyy-MM-dd HH:mm:ss")))
    '                            forms.Add(New FormValue("name", s.submission_pack_name))


    '                            forms.Add(New FormValue("page_id", page.id))
    '                            forms.Add(New FormValue("prefix", ""))
    '                            forms.Add(New FormValue("return_url", ""))
    '                            Dim vs As New List(Of String)


    '                            Dim param = New Dictionary(Of String, String)
    '                            For Each v In forms
    '                                param.Add(v.Name, v.Value)
    '                            Next

    '                            log(JsonConvert.SerializeObject(param))
    '                            log("")
    '                            log("")
    '                            Dim sr As HttpWebRequest
    '                            Dim enc As UTF8Encoding
    '                            Dim postdatabytes As Byte()
    '                            sr = HttpWebRequest.Create("https://nmfs-ersmc-test/business-suite/PostAjax")
    '                            enc = New System.Text.UTF8Encoding()

    '                            postdatabytes = New FormUrlEncodedContent(param).ReadAsByteArrayAsync.Result
    '                            sr.Method = "POST"
    '                            sr.ContentType = "application/x-www-form-urlencoded"
    '                            sr.ContentLength = postdatabytes.Length

    '                            sr.Credentials = CredentialCache.DefaultCredentials
    '                            sr.PreAuthenticate = True

    '                            'sr.Headers.Add()
    '                            Using stream = sr.GetRequestStream()
    '                                stream.Write(postdatabytes, 0, postdatabytes.Length)
    '                            End Using
    '                            Dim result = sr.GetResponse()

    '                            log(result.ToString)
    '                        Catch ex As Exception
    '                            log("      ---- Error: ")
    '                            log("           >> To be added: " & s.reference & " - " & s.submission_pack_name)
    '                            log("      ---- Error: " & ex.Message)
    '                        End Try

    '                    Else
    '                        Try
    '                            log("  >> " & db.processes.Single(Function(f) f.external_reference = s.reference And f.is_deleted = False).reference)
    '                        Catch ex As Exception
    '                            log("  >> NOT IN")
    '                        End Try

    '                    End If

    '                Next
    '            Catch ex As Exception
    '                log(ex.Message)
    '            End Try
    '            ''Update Processes vs Tasks
    '        Next
    '    Catch ex As Exception
    '        log("ExecuteLoadingSingle: Failed: " & ex.Message)
    '        log("ExecuteLoadingSingle: Failed: " & ex.StackTrace)
    '    End Try

    'End Sub


    Public Shared Sub autoCalc(ByVal m As [module], ByVal process As process, ByVal id As Integer, ByVal db As BusinessSuiteEntities)
        ''
        Try
            Dim day_spend2 = 0
            For Each t In db.tasks.Where(Function(f) f.process = id)


                ''Check log
                Dim day_spent = 0

                Dim startD As DateTime = Nothing
                Dim endD As DateTime = Nothing

                Dim hours_spent = 0.0

                For Each tl In db.task_log.Where(Function(ff) ff.task = t.id And Not ff.completion_date Is Nothing)
                    If startD = Nothing Then
                        startD = tl.start_date
                    End If
                    endD = tl.completion_date

                    ''Check if start date and end date is the same
                    If tl.start_date.Value.Date = tl.completion_date.Value.Date Then


                        If tl.completion_date.Value.Hour <= 17 Then
                            Dim span As TimeSpan = tl.completion_date.Value.Subtract(tl.start_date)
                            If tl.completion_date.Value.Hour >= 13 And tl.start_date.Value.Hour <= 13 Then
                                tl.hours_spent = span.TotalMinutes - 60
                                If tl.hours_spent < 0 Then
                                    tl.hours_spent = 0
                                End If
                            Else
                                tl.hours_spent = span.TotalMinutes
                            End If


                        Else
                            Dim w = New DateTime(tl.completion_date.Value.Year, tl.completion_date.Value.Month, tl.completion_date.Value.Day, 17, 0, 0, 0)
                            Dim span As TimeSpan = w.Subtract(tl.start_date)

                            If tl.start_date.Value.Hour < 13 Then
                                tl.hours_spent = span.TotalMinutes - 60
                            Else
                                tl.hours_spent = span.TotalMinutes
                            End If

                        End If

                        tl.hours_spent = tl.hours_spent / 60

                    Else
                        Dim hours = 0.0
                        Dim endDate = New DateTime(tl.completion_date.Value.Year, tl.completion_date.Value.Month, tl.completion_date.Value.Day, 17, 0, 0, 0)
                        For d As Double = tl.start_date.Value.ToOADate To endDate.ToOADate
                            Dim thisDate As Date = DateTime.FromOADate(d)




                            If thisDate.DayOfWeek <> DayOfWeek.Sunday And thisDate.DayOfWeek <> DayOfWeek.Saturday Then
                                'Check Public holidays
                                Dim publicHoliday = False
                                If db.public_holidays.Where(Function(f) f.holiday_date = thisDate.Date).Count = 1 Then
                                    publicHoliday = True
                                End If
                                If Not publicHoliday Then

                                    If thisDate.Date.Day = tl.completion_date.Value.Date.Day And thisDate.Date.Month = tl.completion_date.Value.Date.Month And thisDate.Date.Year = tl.completion_date.Value.Date.Year Then
                                        Dim lastDateStart = New DateTime(thisDate.Year, thisDate.Month, thisDate.Day, 8, 0, 0, 0)
                                        If tl.completion_date.Value.Hour <= 17 Then


                                            Dim span As TimeSpan = tl.completion_date.Value.Subtract(lastDateStart)
                                            'Remove lunch hour
                                            If tl.completion_date.Value.Hour >= 13 Then
                                                hours = hours + span.TotalMinutes - 60
                                            Else
                                                hours = hours + span.TotalMinutes
                                            End If

                                            If tl.completion_date.Value.Hour = 17 Then
                                                hours = hours - tl.completion_date.Value.Minute
                                            End If

                                        Else
                                            Dim w = New DateTime(tl.completion_date.Value.Year, tl.completion_date.Value.Month, tl.completion_date.Value.Day, 17, 0, 0, 0)
                                            Dim span As TimeSpan = w.Subtract(lastDateStart)
                                            hours = hours + span.TotalMinutes - 60
                                        End If

                                    Else


                                        If thisDate.Date.Day = startD.Date.Day And thisDate.Date.Month = startD.Date.Month And thisDate.Date.Year = startD.Date.Year Then

                                            Dim w = New DateTime(thisDate.Year, thisDate.Month, thisDate.Day, 17, 0, 0, 0)
                                            Dim span As TimeSpan = w.Subtract(startD)
                                            If startD.Hour <= 13 Then
                                                hours = hours + span.TotalMinutes - 60

                                            Else
                                                hours = hours + span.TotalMinutes
                                            End If
                                        Else
                                            hours = hours + (8 * 60)
                                        End If

                                    End If

                                End If

                            End If
                        Next
                        tl.hours_spent = hours / 60
                    End If


                Next
            Next

            For Each t In db.tasks.Where(Function(f) f.process = id)
                Try
                    t.days_spent = db.task_log.Where(Function(f) f.task = t.id).Sum(Function(f) f.hours_spent) / 8



                Catch ex As Exception

                End Try
            Next

        Catch ex As Exception
            'log("Task Calculation: Error: " & ex.Message)
            'log(JsonConvert.SerializeObject(ex))
        End Try

    End Sub


    Protected Overrides Sub OnStop()
        stopSignal.Set()

        If t2 IsNot Nothing AndAlso t2.IsAlive Then
            If Not t2.Join(ServiceRuntime.StopWait) Then
                log("Service stop is waiting for the active workflow step to finish.")
                Return
            End If
        End If

        DisposeDataContexts()
        CloseLogWriter()
    End Sub

    'Public Sub execute()

    '    Do
    '        Try
    '            log("Begin Business Suite Update...")
    '            Update()
    '            log("Auto Assiging...")
    '            log("Complete Business Suite Update...")
    '            log("Waiting for " & ConfigurationManager.AppSettings.Get("SleepInMinutes"))
    '            Thread.Sleep(60000 * CInt(ConfigurationManager.AppSettings.Get("SleepInMinutes")))
    '        Catch ex As Exception

    '        End Try

    '    Loop

    'End Sub

    Public Shared Sub log(ByVal msg As String, Optional debug As Boolean = False)
        Try
            Dim d = ConfigurationManager.AppSettings.Get("Debug")
            If d = "1" Then
                debug = True
            End If
            If debug Then
                SyncLock logSync
                    Dim currentDate = Date.Today
                    If logWriter Is Nothing OrElse logWriterDate <> currentDate Then
                        CloseLogWriterUnsafe()
                        IO.Directory.CreateDirectory("C:\Debug")
                        Dim logPath = IO.Path.Combine("C:\Debug", "BusinessSuiteWorkflow-" & currentDate.ToString("yyyy-MM-dd") & ".txt")
                        logWriter = New IO.StreamWriter(logPath, True, Encoding.UTF8) With {.AutoFlush = True}
                        logWriterDate = currentDate
                    End If

                    logWriter.WriteLine(Date.Now.ToString("yyyy-MM-dd HH:mm:ss.fff") & ": " & msg)
                End SyncLock
            End If
        Catch ex As Exception

        End Try

    End Sub

    Private Shared Sub CloseLogWriter()
        SyncLock logSync
            CloseLogWriterUnsafe()
        End SyncLock
    End Sub

    Private Shared Sub CloseLogWriterUnsafe()
        If logWriter IsNot Nothing Then
            logWriter.Dispose()
            logWriter = Nothing
        End If
    End Sub

    Public Sub Update()
        log("Update: Started: ")
        Try


            Dim startDate = Date.Today.AddMonths(CInt(ConfigurationManager.AppSettings.Get("CheckDaysBack")))
            'Dim startDate = Date.Today.AddYears(-5)
            Dim endDate = Date.Today.AddDays(1)

            ' Update entities
            'log("Updating Entities....")
            'Try
            '    '' UpdateEntities()
            '    log("Entities Updated....")
            'Catch ex As Exception
            '    log("Error: Updating Entities")
            'End Try



            'For Each td In db.process_types.Where(Function(f) f.is_deleted = False And Not f.form_type Is Nothing And Not f.process_definition Is Nothing And Not f.entity_group Is Nothing And f.form_type > 0 And f.entity_group > 0)
            For Each td In db.process_types.Where(Function(f) f.is_deleted = False And Not f.form_type Is Nothing And Not f.process_definition Is Nothing And f.form_type > 0)
                log("           >> Checking: " & td.name)


                Dim pdef = db.process_definitions.Single(Function(f) f.id = td.process_definition)

                If pdef.active = True Then
                    Dim groups As New List(Of Integer)
                    If Not td.entity_group Is Nothing And td.entity_group > 0 Then
                        groups.Add(td.entity_group)
                    Else
                        For Each g In db.entity_types
                            groups.Add(g.id)
                        Next
                    End If



                    ''Update Submission packs
                    Try
                        Dim form = db.form_types.Single(Function(f) f.id = td.form_type)
                        log("           >>  Form: " & form.form_name)


                        Try

                            For Each ty In groups
                                Dim entity_type = db.entity_types.Where(Function(f) f.id = ty).Single
                                log("           >>  Entity Type: " & entity_type.name & " (" & entity_type.entity_group_id & ")")
                                log("           >>  Form Type: " & form.form_name & " (" & form.form_type_id & ")")
                                Try
                                    Dim packs = db.GetBusinessSuiteSubmissionPacks2(startDate, endDate, form.form_type_id, entity_type.entity_group_id).ToList()

                                    'Protect the loader from duplicate rows produced by submission-pack joins.
                                    'A reference must only be considered once for a given process type and entity group.
                                    Dim uniquePacks = packs.Where(Function(pack) Not String.IsNullOrWhiteSpace(pack.reference)).GroupBy(Function(pack) pack.reference).Select(Function(packGroup) packGroup.OrderByDescending(Function(pack) pack.submitted_on).First()).ToList()
                                    If packs.Count <> uniquePacks.Count Then
                                        log("           >> Duplicate submission rows removed: " & (packs.Count - uniquePacks.Count).ToString())
                                    End If

                                    For Each s In uniquePacks
                                        'log("           >> Found: " & s.reference & " - " & s.submission_pack_name)
                                        If Not db.processes.Any(Function(f) f.external_reference = s.reference And f.is_deleted = False And f.activity_type = td.id) Then

                                            log("      <><> Checking Condition: " & form.form_name & " - " & s.reference & " - " & s.submission_pack_name)

                                            If Helper.CheckCondition(td, s.reference) = False Then
                                                log("      ---- Condition Not Met: " & form.form_name & " - " & s.reference & " - " & s.submission_pack_name)
                                            Else
                                                log("           >> Form: " & form.form_name)
                                                log("           >> Found: " & s.reference & " - " & s.submission_pack_name)
                                                log("           >> To be added: " & s.reference & " - " & s.submission_pack_name)
                                                Dim m = db.modules.Find(10017)
                                                Dim page = db.module_pages.Find(40596)
                                                log("")
                                                log("")
                                                For Each i In page.module_page_items.Where(Function(f) f.is_show)
                                                    log(" - " & i.module_items.database_column)
                                                Next
                                                Try
                                                    Dim forms As New List(Of FormValue)

                                                    Dim entity = db.entities.SingleOrDefault(Function(f) f.entity_instance_id = s.entity_instance_id And f.is_deleted = False)
                                                    If entity Is Nothing Then
                                                        log("      ---- Application skipped: no active Business Suite entity mapping for entity_instance_id " & s.entity_instance_id.ToString() & " (" & s.reference & ")")
                                                        Continue For
                                                    End If


                                                    forms.Add(New FormValue("process_definition", td.process_definition))
                                                    forms.Add(New FormValue("activity_type", td.id))
                                                    forms.Add(New FormValue("division", td.division))
                                                    forms.Add(New FormValue("entity", entity.id))
                                                    forms.Add(New FormValue("entity_type", entity_type.id))
                                                    forms.Add(New FormValue("external_reference", s.reference))
                                                    If s.created_on.HasValue Then
                                                        forms.Add(New FormValue("created_on", s.created_on.Value.ToString("yyyy-MM-dd HH:mm:ss")))
                                                    End If
                                                    If s.submitted_on.HasValue Then
                                                        forms.Add(New FormValue("submitted_on", s.submitted_on.Value.ToString("yyyy-MM-dd HH:mm:ss")))
                                                    End If
                                                    If s.end_date.HasValue Then
                                                        forms.Add(New FormValue("form_due_date", s.end_date.Value.ToString("yyyy-MM-dd HH:mm:ss")))
                                                    End If
                                                    If s.state_last_changed_on.HasValue Then
                                                        forms.Add(New FormValue("form_action_date", s.state_last_changed_on.Value.ToString("yyyy-MM-dd HH:mm:ss")))
                                                    ElseIf s.submitted_on.HasValue Then
                                                        forms.Add(New FormValue("form_action_date", s.submitted_on.Value.ToString("yyyy-MM-dd HH:mm:ss")))
                                                    End If
                                                    forms.Add(New FormValue("form_status_change", If(String.IsNullOrWhiteSpace(s.action_performed), "0", "1")))
                                                    forms.Add(New FormValue("name", s.submission_pack_name))


                                                    forms.Add(New FormValue("page_id", page.id))
                                                    forms.Add(New FormValue("prefix", ""))
                                                    forms.Add(New FormValue("return_url", ""))
                                                    Dim vs As New List(Of String)


                                                    Dim param = New Dictionary(Of String, String)
                                                    For Each v In forms
                                                        param.Add(v.Name, v.Value)
                                                    Next

                                                    log(JsonConvert.SerializeObject(param))
                                                    log("")
                                                    log("")
                                                    SubmitForm(param)
                                                Catch ex As Exception
                                                    log("      ---- Error: ")
                                                    log("           >> To be added: " & s.reference & " - " & s.submission_pack_name)
                                                    log("      ---- Error: " & ex.Message)
                                                End Try

                                            End If



                                        End If


                                    Next
                                Catch ex As Exception
                                    log("Updating Error: " & ex.Message)
                                    log("Updating Error: " & ex.StackTrace)
                                End Try

                            Next




                        Catch ex As Exception
                            log("Updating: >> Process Type: " & td.name & " >>  Form Type: " & form.form_name & " >> Entity Group: " & td.entity_group & ": Not found...")
                        End Try


                    Catch ex As Exception
                        log("Updating: >>  Process Type: " & td.name & " >> Form Type: " & td.form_type & ": Not found...")
                    End Try

                    ''Update Processes vs Tasks
                Else
                    log("  :: >> Inactive: ")
                End If












            Next

            ''DO AUTO UPDATE:
            Dim mid = 10017
            Dim mm = db.modules.Single(Function(f) f.id = mid)
            If Not mm.data_source Is Nothing And mm.data_source <> "" Then
                ''SQL
                If mm.data_source.StartsWith("SQL:") Then
                    Dim query = mm.data_source.Replace("SQL:", "")
                    ''log("Executing: " & query)
                    Using conn As New SqlConnection(ConfigurationManager.AppSettings.Get("DB"))
                        Using cmd As New SqlCommand(query, conn)
                            cmd.CommandTimeout = ServiceRuntime.CommandTimeoutSeconds
                            conn.Open()
                            cmd.ExecuteNonQuery()
                        End Using
                    End Using
                    ''If id = 1 Then
                    ''    log(" >> Refresh Completed: ")
                    ''Else
                    ''    log(" >> Refresh Not Completed: ")
                    ''End If
                End If

            End If
            ''log("Ending...")
        Catch ex As Exception
            log("Updating Error: " & ex.Message)
            log("Updating Error: " & ex.StackTrace)
        End Try
        log("Update: Completed: ")
    End Sub

    Public Sub UpdateEntities()
        Try
            Dim list = db.GetAllEntities().ToList

            For Each e In list
                Try
                    Dim entity = db.entities.Single(Function(f) f.entity_instance_id = e.entity_instance_id)
                    entity.entity_name = e.Fund_Name
                    entity.license_number = e.License_Number
                    entity.physical_address = e.address
                    entity.entity_group = e.group_id
                    Try
                        entity.de_registration_date = Date.Parse(e.item_deregistrationdate)
                    Catch ex As Exception

                    End Try
                    Try
                        entity.financial_year_end = Date.Parse(e.Financial_Year_End)
                    Catch ex As Exception

                    End Try

                    entity.status = e.Company_Status

                Catch ex As Exception
                    Dim entity As New entity
                    entity.entity_name = e.Fund_Name
                    entity.license_number = e.License_Number
                    entity.physical_address = e.address
                    entity.entity_group = e.group_id
                    entity.entity_instance_id = e.entity_instance_id
                    Try
                        entity.de_registration_date = Date.Parse(e.item_deregistrationdate)
                    Catch ex1 As Exception

                    End Try
                    Try
                        entity.financial_year_end = Date.Parse(e.Financial_Year_End)
                    Catch ex1 As Exception

                    End Try
                    entity.is_deleted = 0
                    entity.date_created = Date.Now
                    entity.status = e.Company_Status

                    db.entities.Add(entity)
                    log(entity.entity_name & " added...")
                End Try


            Next
            db.SaveChanges()
        Catch ex As Exception

        End Try

    End Sub

    Public Sub ExecuteAutoAssign()
        Do While Not stopSignal.IsSet
            Dim cycleTimer = Stopwatch.StartNew()

            Try
                Using runLock = ServiceRuntime.TryAcquireRunLock()
                    If runLock Is Nothing Then
                        log("Workflow cycle skipped because another service instance is running.")
                    Else
                        ResetDataContexts()

                        If ServiceRuntime.IsEnabled("SynchronizeLoadingDates", True) Then
                            RunStep("SynchronizeLoadingDates", Sub()
                                                                           Dim updatedRows = ServiceRuntime.SynchronizeLoadingDates(runLock)
                                                                           log("SynchronizeLoadingDates: updated " & updatedRows.ToString() & " task(s).")
                                                                       End Sub)
                        End If

                        If ServiceRuntime.IsEnabled("UpdateTaskSortOrder") Then
                            RunStep("UpdateTaskSortOrder", Sub()
                                                                     Dim updatedRows = ServiceRuntime.UpdateMissingTaskSortOrders(runLock)
                                                                     log("UpdateTaskSortOrder: updated " & updatedRows.ToString() & " task(s).")
                                                                 End Sub)
                        End If

                        If ServiceRuntime.IsEnabled("ExecuteAutoAssignRun") Then
                            RunStep("ExecuteAutoAssignRun", AddressOf ExecuteAutoAssignRun)
                        End If

                        If ServiceRuntime.IsEnabled("CheckResubmissionExecute") Then
                            RunStep("CheckResubmissionExecute", AddressOf CheckResubmissionExecute)
                        End If

                        If ServiceRuntime.IsEnabled("CheckReturnApplicationExceeding7days") Then
                            RunStep("CheckReturnApplicationExceeding7days", AddressOf CheckReturnApplicationExceeding7days)
                        End If

                        If ServiceRuntime.IsEnabled("CheckReturnedExecute") Then
                            RunStep("CheckReturnedExecute", AddressOf CheckReturnedExecute)
                        End If

                        If ServiceRuntime.IsEnabled("CheckAnnualCancellations") Then
                            RunStep("CheckAnnualCancellations", AddressOf CheckAnnualCancellations)
                        End If

                        If ServiceRuntime.IsEnabled("Update") Then
                            RunStep("Update", AddressOf Update)
                        End If

                        If ServiceRuntime.IsEnabled("TrackStatusChange", True) Then
                            RunStep("TrackStatusChange", Sub() TrackStatusChange(runLock))
                        End If
                    End If
                End Using
            Catch ex As Exception
                log("Workflow cycle failed: " & ex.Message)
                log(ex.StackTrace)
            Finally
                DisposeDataContexts()
            End Try

            cycleTimer.Stop()
            Dim waitTime = ServiceRuntime.PollInterval.Subtract(cycleTimer.Elapsed)
            If waitTime < TimeSpan.Zero Then
                waitTime = TimeSpan.Zero
            End If

            log("Workflow cycle completed in " & cycleTimer.Elapsed.ToString("c") &
                ". Next cycle in " & waitTime.ToString("c") & ".")
            stopSignal.Wait(waitTime)
        Loop

        DisposeDataContexts()
        CloseLogWriter()

    End Sub

    Private Sub RunStep(stepName As String, work As Action)
        Dim timer = Stopwatch.StartNew()
        log(stepName & ": Started")

        Try
            work()
            log(stepName & ": Completed in " & timer.Elapsed.ToString("c"))
        Catch ex As Exception
            log(stepName & ": Failed: " & ex.Message)
            log(stepName & ": Failed: " & ex.StackTrace)
        End Try
    End Sub

    Public Sub TrackStatusChange(connection As SqlConnection)
        Dim processModule = db.modules.FirstOrDefault(
            Function(value) Not value.is_deleted AndAlso
                            (value.slug = "processes" OrElse value.database_table = "processes"))
        If processModule Is Nothing Then
            Throw New InvalidOperationException("TrackStatusChange: the Processes module is not configured.")
        End If

        Dim trackingPage = db.module_pages.FirstOrDefault(
            Function(value) value.module_id = processModule.id AndAlso
                            value.name = "Track Status Change" AndAlso
                            value.page_type_id = 2)
        If trackingPage Is Nothing Then
            Throw New InvalidOperationException(
                "TrackStatusChange: run 20261014-track-process-external-status.sql before enabling this service step.")
        End If

        Dim processIds = ServiceRuntime.GetPendingStatusChangeProcessIds(connection)
        If processIds.Count = 0 Then
            log("TrackStatusChange: no pending external-status changes found.")
            Return
        End If

        Dim completed = 0
        Dim failed = 0
        For Each processId In processIds
            If stopSignal.IsSet Then Exit For

            Dim form As New List(Of FormValue) From {
                New FormValue("form_status_change", "0")
            }

            Dim submitted = PostForm(form, trackingPage, processId.ToString(CultureInfo.InvariantCulture))
            Dim remainsPending = ServiceRuntime.IsStatusChangePending(connection, processId)
            If submitted AndAlso Not remainsPending Then
                completed += 1
                log("TrackStatusChange: processed process " & processId.ToString(CultureInfo.InvariantCulture) & ".")
            Else
                failed += 1
                log("TrackStatusChange: process " & processId.ToString(CultureInfo.InvariantCulture) &
                    " remains pending because the workflow page failed.")
            End If
        Next

        log("TrackStatusChange: processed " & completed.ToString(CultureInfo.InvariantCulture) &
            " of " & processIds.Count.ToString(CultureInfo.InvariantCulture) &
            " pending change(s); failures: " & failed.ToString(CultureInfo.InvariantCulture) & ".")
    End Sub

    Private Sub ResetDataContexts()
        DisposeDataContexts()
        db = New BusinessSuiteEntities()
        vsab = New VSABEntities()
        rs = New ReportingSuiteEntities()
    End Sub

    Private Sub DisposeDataContexts()
        If db IsNot Nothing Then
            db.Dispose()
            db = Nothing
        End If

        If vsab IsNot Nothing Then
            vsab.Dispose()
            vsab = Nothing
        End If

        If rs IsNot Nothing Then
            rs.Dispose()
            rs = Nothing
        End If
    End Sub

    Public Sub UpdateTaskSortOrder()
        Using connection As New SqlConnection(ConfigurationManager.AppSettings.Get("DB"))
            connection.Open()
            ServiceRuntime.UpdateMissingTaskSortOrders(connection)
        End Using
    End Sub

    Public Sub ExecuteAutoAssignRun()
        Dim taskIds = ServiceRuntime.GetEligibleTaskIds()

        If taskIds.Count = 0 Then
            log("ExecuteAutoAssignRun: no eligible tasks found.")
            Return
        End If

        Dim tasksById = db.tasks.
            Where(Function(taskRow) taskIds.Contains(taskRow.id)).
            ToList().
            ToDictionary(Function(taskRow) taskRow.id)

        Dim processIds = tasksById.Values.
            Where(Function(taskRow) taskRow.process.HasValue).
            Select(Function(taskRow) taskRow.process.Value).
            Distinct().
            ToList()
        Dim processesById = db.processes.
            Where(Function(processRow) processIds.Contains(processRow.id)).
            ToList().
            ToDictionary(Function(processRow) processRow.id)

        Dim definitionIds = tasksById.Values.
            Where(Function(taskRow) taskRow.task_definition.HasValue).
            Select(Function(taskRow) taskRow.task_definition.Value).
            Distinct().
            ToList()
        Dim definitionsById = db.task_definitions.
            Where(Function(definition) definitionIds.Contains(definition.id)).
            ToList().
            ToDictionary(Function(definition) definition.id)

        Dim processTypeIds = processesById.Values.
            Where(Function(processRow) processRow.activity_type.HasValue).
            Select(Function(processRow) processRow.activity_type.Value).
            Distinct().
            ToList()
        Dim processTypesById = db.process_types.
            Where(Function(processType) processTypeIds.Contains(processType.id)).
            ToList().
            ToDictionary(Function(processType) processType.id)
        Dim assignmentPage = db.module_pages.Single(Function(page) page.id = 10095)

        Dim count = 0
        For Each taskId In taskIds
            Try
                Dim task As task = Nothing
                If Not tasksById.TryGetValue(taskId, task) OrElse
                   Not task.process.HasValue OrElse
                   Not task.task_definition.HasValue Then
                    log("ExecuteAutoAssignRun: task " & taskId.ToString() & " has incomplete configuration and was skipped.")
                    Continue For
                End If

                Dim task_definition As task_definitions = Nothing
                Dim process As process = Nothing
                If Not definitionsById.TryGetValue(task.task_definition.Value, task_definition) OrElse
                   Not processesById.TryGetValue(task.process.Value, process) OrElse
                   Not process.activity_type.HasValue Then
                    log("ExecuteAutoAssignRun: task " & taskId.ToString() & " has no valid definition or process and was skipped.")
                    Continue For
                End If

                Dim process_type As process_types = Nothing
                If Not processTypesById.TryGetValue(process.activity_type.Value, process_type) Then
                    log("ExecuteAutoAssignRun: process " & process.id.ToString() & " has no valid process type and was skipped.")
                    Continue For
                End If

                If process_type.enable_auto_assign = True Then
                    log(" >> Found: (" & process.external_reference & ": " & process.name & ") >> (" & task.id & ": " & task.name & ") " & task.task_definition)
                    ''First Task
                    log(" || Sort Order: " & task_definition.sort_order)
                    If task_definition.sort_order = 1 Then
                        ''Check if ML

                        If process.activity_type = 24 Then
                            log(" || " & process_type.name)
                            Dim app = db.application_allocations.Where(Function(f) f.enabled = True).OrderBy(Function(f) f.date_allocated).First()

                            Dim agent = db.users.Single(Function(f) f.id = app.analyst)
                            log("  | >> Next Agent: (" & agent.username & ")")
                            Dim form As New List(Of FormValue)
                            form.Add(New FormValue("description_of_activity", ""))
                            form.Add(New FormValue("assigned_to", agent.id))
                            form.Add(New FormValue("cc_agents", ""))
                            form.Add(New FormValue("priority", "2"))


                            PostForm(form, assignmentPage, task.id)

                            task = db.tasks.Single(Function(f) f.id = task.id)
                            app.date_allocated = Date.Now
                            app.application = process.id

                            db.SaveChanges()

                        Else
                            log("Assigning Type: " & process_type.assigning_type)
                            If process_type.assigning_type = "User Cluster" Then
                                log(" >> Assessing: " & process_type.assigning_type)
                                log(" || " & process_type.name & "(Task ID: " & task.id & ") : Assigning Type: " & process_type.assigning_type)
                                Dim agents = db.GetNextTaskDefinitionAgent(task_definition.id).ToList.Distinct()
                                log("Agents (" & process.external_reference & "): " & Newtonsoft.Json.JsonConvert.SerializeObject(agents))
                                Dim clusterAgents As New List(Of Integer)
                                For Each cluster In db.role_clusters.Where(Function(f) f.is_deleted = False And f.division = process.division And f.role = task.role)
                                    If process.division = cluster.division Then
                                        log("Checking Cluster:  (" & cluster.cluster_name & ")")
                                        log(">> Groups:  (" & cluster.entity_groups & ")")
                                        log(">> Users:  (" & cluster.cluster_users & ")")
                                        log(">> Supervisor:  (" & cluster.cluster_supervisor & ")")
                                        If Not cluster.entity_groups Is Nothing And String.IsNullOrEmpty(cluster.entity_groups) = False And Not cluster.cluster_users Is Nothing And String.IsNullOrEmpty(cluster.cluster_users) = False And Not cluster.cluster_supervisor Is Nothing And String.IsNullOrEmpty(cluster.cluster_supervisor) = False Then
                                            For Each entity_type In cluster.entity_groups.Split(",")
                                                Try
                                                    If CInt(entity_type) = process.entity_type Then
                                                        For Each cluster_user In cluster.cluster_users.Split(",")
                                                            clusterAgents.Add(CInt(cluster_user))
                                                        Next
                                                    End If
                                                Catch ex As Exception
                                                    log("Cluster Agents: Error (" & process.external_reference & "): " & ex.Message)
                                                    log("Cluster Agents: Error (" & process.external_reference & "): " & ex.StackTrace)
                                                End Try
                                            Next
                                        End If

                                    End If

                                Next
                                log("Cluster Agents (" & process.external_reference & "): " & Newtonsoft.Json.JsonConvert.SerializeObject(clusterAgents))
                                For Each a In agents
                                    Dim assigned = False
                                    For Each ca In clusterAgents
                                        If a = ca Then
                                            Dim agent = db.users.Single(Function(f) f.id = a)
                                            log("  | >> Next Agent: (" & agent.username & ")")
                                            Dim form As New List(Of FormValue)
                                            form.Add(New FormValue("description_of_activity", ""))
                                            form.Add(New FormValue("assigned_to", agent.id))
                                            form.Add(New FormValue("cc_agents", ""))
                                            form.Add(New FormValue("priority", "2"))
                                            PostForm(form, assignmentPage, task.id)
                                            assigned = True
                                        End If
                                        If assigned Then
                                            Exit For
                                        End If

                                    Next
                                    If assigned Then
                                        Exit For
                                    End If


                                Next
                            End If

                            If process_type.assigning_type = "User Portfolio" Then
                                log(" >> Assessing: " & process_type.assigning_type)
                                log(" || " & process_type.name & "(Task ID: " & task.id & ") : Assigning Type: " & process_type.assigning_type)

                                Dim userPortfolio As New List(Of Integer)
                                For Each up In db.user_portfolios.Where(Function(f) f.status = True And f.portfolio_role = task_definition.role)
                                    log("  >>|| Portfolio: " & up.portfolio_name)
                                    If up.division = process.division Then
                                        log("  >>|| Process Division: " & process.division)
                                        log("  >>|| Portfolio Division: " & up.division)
                                        log("  >>|| Portfolio Entities: " & up.portfolio_entities)
                                        If Not up.portfolio_entities Is Nothing And up.portfolio_entities <> "" Then
                                            For Each pentity In up.portfolio_entities.Split(",")
                                                Dim pentityID = CLng(pentity)
                                                If process.entity = pentityID Then
                                                    userPortfolio.Add(up.portfolio_user)
                                                    log("  >>|| Portfolio User Add: " & up.portfolio_user)
                                                End If
                                            Next
                                        End If
                                    End If

                                Next
                                Dim assigned = False
                                For Each up In userPortfolio

                                    Dim agent = db.users.Single(Function(f) f.id = up)
                                    log("  | >> Next Agent: (" & agent.username & ")")
                                    Dim form As New List(Of FormValue)
                                    form.Add(New FormValue("description_of_activity", ""))
                                    form.Add(New FormValue("assigned_to", agent.id))
                                    form.Add(New FormValue("cc_agents", ""))
                                    form.Add(New FormValue("priority", "2"))
                                    PostForm(form, assignmentPage, task.id)
                                    assigned = True

                                    If assigned Then
                                        Exit For
                                    End If
                                Next
                            End If
                            log(" >> Assession Completed: " & process_type.assigning_type & " <<  ")
                        End If
                    End If
                    ''For later tasks, preserve continuity with the last completed task first.
                    ''If that user/role path cannot resolve an assignee, use the application's
                    ''role-cluster or user-portfolio membership as the deterministic fallback.
                    If task_definition.sort_order <> 1 Then
                        Dim assigned = TryAssignFromLastCompletedTask(task, task_definition, process, process_type, assignmentPage)
                        If Not assigned Then
                            log("  | >> Previous-task assignment was not resolved. Assessing application ownership.")
                            assigned = TryAssignFromApplicationOwnership(task, task_definition, process, process_type, assignmentPage)
                        End If
                        If Not assigned Then
                            log("  | >> No eligible role cluster or user portfolio assignment was found for task " & task.id.ToString(CultureInfo.InvariantCulture) & ".")
                        End If
                    End If
                    count = count + 1
                End If


            Catch ex As Exception
                log(" >> Exception: " & ex.Message)
                log(ex.StackTrace)
                log(Newtonsoft.Json.JsonConvert.SerializeObject(ex))
            End Try




        Next
    End Sub

    Private Function TryAssignFromLastCompletedTask(ByVal taskRow As task,
                                                    ByVal definition As task_definitions,
                                                    ByVal processRow As process,
                                                    ByVal processType As process_types,
                                                    ByVal assignmentPage As module_pages) As Boolean
        Try
            Dim lastTask = db.tasks.
                Where(Function(value) value.process = taskRow.process AndAlso
                                      value.id <> taskRow.id AndAlso
                                      value.status = 4 AndAlso
                                      Not value.is_deleted AndAlso
                                      value.assigned_to.HasValue AndAlso
                                      value.task_definition.HasValue).
                OrderByDescending(Function(value) value.completion_date).
                ThenByDescending(Function(value) value.sort_order).
                ThenByDescending(Function(value) value.id).
                FirstOrDefault()

            If lastTask Is Nothing Then
                log("  | >> No completed task with an assignee was found for process " & processRow.id.ToString(CultureInfo.InvariantCulture) & ".")
                Return False
            End If

            Dim lastDefinition = db.task_definitions.FirstOrDefault(Function(value) value.id = lastTask.task_definition.Value AndAlso Not value.is_deleted)
            If lastDefinition Is Nothing Then
                log("  | >> The last completed task definition could not be loaded.")
                Return False
            End If

            Dim currentRole = EffectiveTaskRole(taskRow, definition)
            Dim lastRole = EffectiveTaskRole(lastTask, lastDefinition)
            If Not currentRole.HasValue OrElse Not lastRole.HasValue Then
                log("  | >> The current or previous task role is not configured.")
                Return False
            End If

            If currentRole.Value = lastRole.Value Then
                Return AssignAutoTask(taskRow, lastTask.assigned_to.Value, assignmentPage, "last completed task user for the same role")
            End If

            Dim assigningType = (processType.assigning_type & String.Empty).Trim()
            If assigningType.Equals("User Cluster", StringComparison.OrdinalIgnoreCase) Then
                Dim cluster = FindRoleClusterForPreviousUser(processRow, lastRole.Value, lastTask.assigned_to.Value)
                If cluster IsNot Nothing Then
                    If cluster.next_cluster.HasValue Then
                        Dim nextCluster = db.role_clusters.FirstOrDefault(Function(value) value.id = cluster.next_cluster.Value AndAlso Not value.is_deleted)
                        If nextCluster IsNot Nothing AndAlso nextCluster.role = currentRole AndAlso RoleClusterMatchesApplication(nextCluster, processRow) Then
                            Dim nextUser = SelectRoleClusterUser(nextCluster, taskRow)
                            If nextUser.HasValue AndAlso AssignAutoTask(taskRow, nextUser.Value, assignmentPage, "next role cluster from the last completed task") Then Return True
                        End If
                    End If
                    If processType.supervisory_role = currentRole AndAlso cluster.cluster_supervisor.HasValue Then
                        If AssignAutoTask(taskRow, cluster.cluster_supervisor.Value, assignmentPage, "supervisor of the last completed task role cluster") Then Return True
                    End If
                End If
            ElseIf assigningType.Equals("User Portfolio", StringComparison.OrdinalIgnoreCase) Then
                Dim portfolio = FindPortfolioForPreviousUser(processRow, lastRole.Value, lastTask.assigned_to.Value)
                If portfolio IsNot Nothing AndAlso processType.supervisory_role = currentRole AndAlso portfolio.portfolio_supervisor.HasValue Then
                    If AssignAutoTask(taskRow, portfolio.portfolio_supervisor.Value, assignmentPage, "supervisor of the last completed task user portfolio") Then Return True
                End If
            End If

            log("  | >> Last completed task " & lastTask.id.ToString(CultureInfo.InvariantCulture) & " did not determine an assignee for role " & currentRole.Value.ToString(CultureInfo.InvariantCulture) & ".")
            Return False
        Catch ex As Exception
            log("  | >> Previous-task assignment failed: " & ex.Message)
            log(ex.StackTrace)
            Return False
        End Try
    End Function

    Private Function TryAssignFromApplicationOwnership(ByVal taskRow As task,
                                                       ByVal definition As task_definitions,
                                                       ByVal processRow As process,
                                                       ByVal processType As process_types,
                                                       ByVal assignmentPage As module_pages) As Boolean
        Try
            Dim assigningType = (processType.assigning_type & String.Empty).Trim()
            If assigningType.Equals("User Cluster", StringComparison.OrdinalIgnoreCase) Then
                log("  | >> Resolving application ownership through role clusters.")
                If TryAssignFromRoleCluster(taskRow, definition, processRow, assignmentPage) Then Return True
                log("  | >> No role-cluster assignment was resolved; checking user portfolios.")
                Return TryAssignFromUserPortfolio(taskRow, definition, processRow, assignmentPage)
            End If
            If assigningType.Equals("User Portfolio", StringComparison.OrdinalIgnoreCase) Then
                log("  | >> Resolving application ownership through user portfolios.")
                If TryAssignFromUserPortfolio(taskRow, definition, processRow, assignmentPage) Then Return True
                log("  | >> No user-portfolio assignment was resolved; checking role clusters.")
                Return TryAssignFromRoleCluster(taskRow, definition, processRow, assignmentPage)
            End If

            log("  | >> Assigning type is not configured; checking role clusters, then user portfolios.")
            If TryAssignFromRoleCluster(taskRow, definition, processRow, assignmentPage) Then Return True
            Return TryAssignFromUserPortfolio(taskRow, definition, processRow, assignmentPage)
        Catch ex As Exception
            log("  | >> Application ownership assignment failed: " & ex.Message)
            log(ex.StackTrace)
            Return False
        End Try
    End Function

    Private Function TryAssignFromRoleCluster(ByVal taskRow As task,
                                              ByVal definition As task_definitions,
                                              ByVal processRow As process,
                                              ByVal assignmentPage As module_pages) As Boolean
        Dim roleId = EffectiveTaskRole(taskRow, definition)
        Dim divisionId = ProcessDivisionId(processRow)
        If Not roleId.HasValue OrElse Not divisionId.HasValue OrElse Not processRow.entity_type.HasValue Then
            log("  | >> Role-cluster fallback requires task role, process division, and entity type.")
            Return False
        End If

        Dim today = Date.Today
        Dim clusters = db.role_clusters.
            Where(Function(value) Not value.is_deleted AndAlso value.role = roleId.Value AndAlso value.division = divisionId.Value).
            ToList().
            Where(Function(value) (Not value.start_date.HasValue OrElse value.start_date.Value.Date <= today) AndAlso
                                   (Not value.end_date.HasValue OrElse value.end_date.Value.Date >= today) AndAlso
                                   CsvContains(value.entity_groups, processRow.entity_type.Value)).
            OrderBy(Function(value) ParseCsvIds(value.entity_groups).Count).
            ThenBy(Function(value) value.id).
            ToList()

        For Each cluster In clusters
            Dim userId = SelectRoleClusterUser(cluster, taskRow)
            If userId.HasValue Then
                log("  | >> Application matched role cluster: " & cluster.cluster_name)
                If AssignAutoTask(taskRow, userId.Value, assignmentPage, "application role cluster " & cluster.cluster_name) Then Return True
            End If
        Next

        log("  | >> No active role cluster matched division " & divisionId.Value.ToString(CultureInfo.InvariantCulture) & ", role " & roleId.Value.ToString(CultureInfo.InvariantCulture) & ", and entity type " & processRow.entity_type.Value.ToString(CultureInfo.InvariantCulture) & ".")
        Return False
    End Function

    Private Function TryAssignFromUserPortfolio(ByVal taskRow As task,
                                                ByVal definition As task_definitions,
                                                ByVal processRow As process,
                                                ByVal assignmentPage As module_pages) As Boolean
        Dim roleId = EffectiveTaskRole(taskRow, definition)
        Dim divisionId = ProcessDivisionId(processRow)
        If Not roleId.HasValue OrElse Not divisionId.HasValue OrElse Not processRow.entity.HasValue Then
            log("  | >> User-portfolio fallback requires task role, process division, and entity.")
            Return False
        End If

        Dim portfolios = db.user_portfolios.
            Where(Function(value) Not value.is_deleted AndAlso value.status = True AndAlso value.portfolio_role = roleId.Value AndAlso value.division = divisionId.Value).
            ToList().
            Where(Function(value) value.entity = processRow.entity OrElse CsvContains(value.portfolio_entities, processRow.entity.Value)).
            OrderByDescending(Function(value) value.entity.HasValue AndAlso value.entity.Value = processRow.entity.Value).
            ThenBy(Function(value) value.id).
            ToList()

        For Each portfolio In portfolios
            Dim userId As Nullable(Of Long) = Nothing
            If portfolio.portfolio_user.HasValue AndAlso IsAssignableUser(portfolio.portfolio_user.Value) Then
                userId = portfolio.portfolio_user.Value
            ElseIf portfolio.portfolio_supervisor.HasValue AndAlso IsAssignableUser(portfolio.portfolio_supervisor.Value) Then
                userId = portfolio.portfolio_supervisor.Value
            End If
            If userId.HasValue Then
                log("  | >> Application matched user portfolio: " & portfolio.portfolio_name)
                If AssignAutoTask(taskRow, userId.Value, assignmentPage, "application user portfolio " & portfolio.portfolio_name) Then Return True
            End If
        Next

        log("  | >> No active user portfolio matched division " & divisionId.Value.ToString(CultureInfo.InvariantCulture) & ", role " & roleId.Value.ToString(CultureInfo.InvariantCulture) & ", and entity " & processRow.entity.Value.ToString(CultureInfo.InvariantCulture) & ".")
        Return False
    End Function

    Private Function FindRoleClusterForPreviousUser(ByVal processRow As process, ByVal roleId As Long, ByVal userId As Long) As role_clusters
        Dim divisionId = ProcessDivisionId(processRow)
        If Not divisionId.HasValue OrElse Not processRow.entity_type.HasValue Then Return Nothing
        Dim today = Date.Today
        Return db.role_clusters.
            Where(Function(value) Not value.is_deleted AndAlso value.role = roleId AndAlso value.division = divisionId.Value).
            OrderBy(Function(value) value.id).
            ToList().
            FirstOrDefault(Function(value) (Not value.start_date.HasValue OrElse value.start_date.Value.Date <= today) AndAlso
                                           (Not value.end_date.HasValue OrElse value.end_date.Value.Date >= today) AndAlso
                                           CsvContains(value.entity_groups, processRow.entity_type.Value) AndAlso
                                           (CsvContains(value.cluster_users, userId) OrElse CsvContains(value.cluster_alternative_users, userId) OrElse value.cluster_supervisor = userId))
    End Function

    Private Function FindPortfolioForPreviousUser(ByVal processRow As process, ByVal roleId As Long, ByVal userId As Long) As user_portfolios
        Dim divisionId = ProcessDivisionId(processRow)
        If Not divisionId.HasValue OrElse Not processRow.entity.HasValue Then Return Nothing
        Return db.user_portfolios.
            Where(Function(value) Not value.is_deleted AndAlso value.status = True AndAlso value.portfolio_role = roleId AndAlso value.division = divisionId.Value).
            OrderBy(Function(value) value.id).
            ToList().
            FirstOrDefault(Function(value) (value.entity = processRow.entity OrElse CsvContains(value.portfolio_entities, processRow.entity.Value)) AndAlso
                                           (value.portfolio_user = userId OrElse value.portfolio_supervisor = userId))
    End Function

    Private Function SelectRoleClusterUser(ByVal cluster As role_clusters, ByVal taskRow As task) As Nullable(Of Long)
        Dim preferredUsers As New List(Of Long)()
        Try
            If taskRow.task_definition.HasValue AndAlso taskRow.process.HasValue Then
                preferredUsers = db.GetClusterNextTaskDefinitionAgent(taskRow.task_definition.Value, taskRow.process.Value).
                    Where(Function(value) value.HasValue).
                    Select(Function(value) value.Value).
                    Distinct().
                    ToList()
            End If
            If preferredUsers.Count = 0 AndAlso taskRow.task_definition.HasValue Then
                preferredUsers = db.GetNextTaskDefinitionAgent(taskRow.task_definition.Value).
                    Where(Function(value) value.HasValue).
                    Select(Function(value) value.Value).
                    Distinct().
                    ToList()
            End If
        Catch ex As Exception
            log("  | >> Workload candidate lookup failed for task " & taskRow.id.ToString(CultureInfo.InvariantCulture) & ": " & ex.Message)
        End Try

        Dim primaryUsers = ParseCsvIds(cluster.cluster_users)
        Dim alternativeUsers = ParseCsvIds(cluster.cluster_alternative_users)
        For Each userId In preferredUsers
            If primaryUsers.Contains(userId) AndAlso IsAssignableUser(userId) Then Return userId
        Next
        For Each userId In primaryUsers
            If IsAssignableUser(userId) Then Return userId
        Next
        For Each userId In preferredUsers
            If alternativeUsers.Contains(userId) AndAlso IsAssignableUser(userId) Then Return userId
        Next
        For Each userId In alternativeUsers
            If IsAssignableUser(userId) Then Return userId
        Next
        If cluster.cluster_supervisor.HasValue AndAlso IsAssignableUser(cluster.cluster_supervisor.Value) Then Return cluster.cluster_supervisor.Value
        Return Nothing
    End Function

    Private Function RoleClusterMatchesApplication(ByVal cluster As role_clusters, ByVal processRow As process) As Boolean
        Dim divisionId = ProcessDivisionId(processRow)
        Return divisionId.HasValue AndAlso processRow.entity_type.HasValue AndAlso
               cluster.division = divisionId.Value AndAlso
               CsvContains(cluster.entity_groups, processRow.entity_type.Value)
    End Function

    Private Function EffectiveTaskRole(ByVal taskRow As task, ByVal definition As task_definitions) As Nullable(Of Long)
        If taskRow.role.HasValue Then Return taskRow.role
        If definition IsNot Nothing Then Return definition.role
        Return Nothing
    End Function

    Private Function ProcessDivisionId(ByVal processRow As process) As Nullable(Of Long)
        Dim divisionId As Long
        If Long.TryParse((processRow.division & String.Empty).Trim(), divisionId) AndAlso divisionId > 0 Then Return divisionId
        Return Nothing
    End Function

    Private Function ParseCsvIds(ByVal value As String) As List(Of Long)
        Dim result As New List(Of Long)()
        For Each token In (value & String.Empty).Split(New Char() {","c, ";"c}, StringSplitOptions.RemoveEmptyEntries)
            Dim parsed As Long
            If Long.TryParse(token.Trim(), parsed) AndAlso parsed > 0 AndAlso Not result.Contains(parsed) Then result.Add(parsed)
        Next
        Return result
    End Function

    Private Function CsvContains(ByVal value As String, ByVal expectedId As Long) As Boolean
        Return ParseCsvIds(value).Contains(expectedId)
    End Function

    Private Function IsAssignableUser(ByVal userId As Long) As Boolean
        Return userId > 0 AndAlso db.users.Any(Function(value) value.id = userId AndAlso Not value.is_deleted)
    End Function

    Private Function AssignAutoTask(ByVal taskRow As task, ByVal userId As Long, ByVal assignmentPage As module_pages, ByVal source As String) As Boolean
        Dim agent = db.users.FirstOrDefault(Function(value) value.id = userId AndAlso Not value.is_deleted)
        If agent Is Nothing Then
            log("  | >> Assignment candidate " & userId.ToString(CultureInfo.InvariantCulture) & " is not an active user.")
            Return False
        End If

        log("  | >> Next Agent: (" & agent.username & ") via " & source)
        Dim form As New List(Of FormValue) From {
            New FormValue("description_of_activity", ""),
            New FormValue("assigned_to", agent.id),
            New FormValue("cc_agents", ""),
            New FormValue("priority", "2")
        }
        Return PostForm(form, assignmentPage, taskRow.id.ToString(CultureInfo.InvariantCulture))
    End Function

    Public Function PostForm(ByVal forms As List(Of FormValue), ByVal p As module_pages, ByVal dataID As String) As Boolean
        Try

            Dim vs As New List(Of String)

            forms.Add(New FormValue("page_id", p.id))
            forms.Add(New FormValue("data_id", dataID))
            forms.Add(New FormValue("prefix", ""))
            forms.Add(New FormValue("return_url", ""))

            Dim param = New Dictionary(Of String, String)
            For Each v In forms
                param.Add(v.Name, v.Value)
            Next

            log(JsonConvert.SerializeObject(param))
            log("")
            log("")
            SubmitForm(param)
            Return True
        Catch ex As Exception
            log("      ---- Error: ")
            log("      ---- Error: " & ex.Message)
            Return False
        End Try

    End Function

    Private Sub SubmitForm(parameters As IDictionary(Of String, String))
        Dim postData As Byte()
        Using content As New FormUrlEncodedContent(parameters)
            postData = content.ReadAsByteArrayAsync().GetAwaiter().GetResult()
        End Using

        Dim request = DirectCast(WebRequest.Create(ConfigurationManager.AppSettings.Get("PostAjax")), HttpWebRequest)
        request.Method = "POST"
        request.ContentType = "application/x-www-form-urlencoded"
        request.ContentLength = postData.Length
        request.UseDefaultCredentials = True
        request.PreAuthenticate = True
        request.Timeout = ServiceRuntime.PostTimeoutMilliseconds
        request.ReadWriteTimeout = ServiceRuntime.PostTimeoutMilliseconds

        Using stream = request.GetRequestStream()
            stream.Write(postData, 0, postData.Length)
        End Using

        Using response = DirectCast(request.GetResponse(), HttpWebResponse)
            log("PostAjax completed with HTTP " & CInt(response.StatusCode).ToString() & ".")
        End Using
    End Sub

    Public Async Function FetchAndInsertRatesAsync(Optional baseCode As String = "NAD") As System.Threading.Tasks.Task
        Dim url = $"https://open.er-api.com/v6/latest/{baseCode}"

        Dim json As String
        Using http As New HttpClient()
            http.Timeout = TimeSpan.FromSeconds(30)
            json = Await http.GetStringAsync(url)
        End Using

        Dim opts As New JsonSerializerOptions With {.PropertyNameCaseInsensitive = True}
        Dim data = Text.Json.JsonSerializer.Deserialize(Of ErApiResponse)(json, opts)
        If data Is Nothing OrElse data.rates Is Nothing OrElse Not String.Equals(data.result, "success", StringComparison.OrdinalIgnoreCase) Then
            Throw New ApplicationException("FX API returned no data or non-success result.")
        End If

        ' Use API timestamp (UTC) -> date only
        Dim rateDate As Date = DateTimeOffset.FromUnixTimeSeconds(data.time_last_update_unix).UtcDateTime.Date

        Using cn As New SqlConnection(ConnString)
            cn.Open()

            ' Parameterized insert that SKIPS duplicates for the same base+quote+date
            Dim sql As String =
"INSERT INTO dbo.exchange_rates
 (base_code, quoate_code, rate, rate_date, [source], note, child_module, child_data, is_deleted, date_created, created_by)
SELECT @base_code, @quote_code, @rate, @rate_date, @source, @note, NULL, NULL, 0, SYSUTCDATETIME(), NULL
WHERE NOT EXISTS (
    SELECT 1
    FROM dbo.exchange_rates er
    WHERE er.is_deleted = 0
      AND er.base_code = @base_code
      AND er.quoate_code = @quote_code
      AND er.rate_date = @rate_date
);"

            Using cmd As New SqlCommand(sql, cn)
                cmd.Parameters.Add("@base_code", SqlDbType.VarChar, -1)
                cmd.Parameters.Add("@quote_code", SqlDbType.VarChar, -1)
                cmd.Parameters.Add("@rate", SqlDbType.VarChar, -1)
                cmd.Parameters.Add("@rate_date", SqlDbType.Date)
                cmd.Parameters.Add("@source", SqlDbType.VarChar, -1)
                cmd.Parameters.Add("@note", SqlDbType.VarChar, -1)

                cmd.Parameters("@base_code").Value = data.base_code
                cmd.Parameters("@source").Value = "open.er-api.com"
                cmd.Parameters("@note").Value = DBNull.Value
                cmd.Parameters("@rate_date").Value = rateDate

                For Each kvp In data.rates
                    Dim quote As String = kvp.Key

                    ' Keep only your desired set; remove this If to store all
                    If Not KeepSymbols.Contains(quote) Then Continue For

                    Dim rateStr As String = kvp.Value.ToString(CultureInfo.InvariantCulture)

                    cmd.Parameters("@quote_code").Value = quote
                    cmd.Parameters("@rate").Value = rateStr

                    cmd.ExecuteNonQuery()
                Next
            End Using
        End Using
    End Function
End Class

Public Class FormValue
    Public Name As String
    Public Value As String

    Public Sub New(ByVal n As String, ByVal v As String)
        Name = n
        Value = v

    End Sub

End Class
Public Class ErApiResponse
    Public Property result As String
    Public Property base_code As String
    Public Property time_last_update_unix As Long
    Public Property rates As Dictionary(Of String, Decimal)
End Class
