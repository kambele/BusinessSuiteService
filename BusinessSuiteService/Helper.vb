Imports System.Configuration
Imports System.Data.SqlClient
Imports System.Text.RegularExpressions

Public Class Helper

    Public Shared Function CheckCondition(ByVal processType As process_types, ByVal externalReference As String) As Boolean
        Dim result As Boolean = False

        If Not processType.condition Is Nothing And processType.condition <> "" Then
            Dim input As String = processType.condition
            'Service1.log("CheckCondition: Condition Inpit: " & input)
            Dim parseCondition As String = input

            ' Regex to match text between [ and ]
            Dim matches As MatchCollection = Regex.Matches(input, "\[(.*?)\]")

            Dim results As New List(Of String)
            For Each m As Match In matches
                results.Add(m.Groups(1).Value)
            Next
            ' Print results
            For Each s In results
                Dim datatable = s.Split(";")
                Dim databaseSource = datatable(0).Trim()
                Dim databaseColumn = datatable(1).Trim()
                Dim databaseTable = datatable(2).Trim()
                Dim v = GetQueryValue(GetQuery(databaseSource, databaseColumn, databaseTable, externalReference))

                parseCondition = parseCondition.Replace("[" & s & "]", "'" & v & "'")

            Next
            Service1.log("CheckCondition: Condition Parsed: " & parseCondition)

            result = ExecuteConditionQuery(parseCondition)

        Else
            result = True
        End If

        Return result
    End Function
    Public Shared Function GetQueryValue(ByVal query As String) As String
        Dim value As String = ""
        Dim conn As New SqlConnection(ConfigurationManager.AppSettings.Get("DB"))
        Service1.log("GetQueryValue: Query: " & query)
        Try

            conn.Open()
            Dim a As SqlDataAdapter = New SqlDataAdapter(query, conn)

            Dim dt As DataTable = New DataTable()
            a.Fill(dt)
            For Each row As DataRow In dt.Rows
                Dim v = row(0)
                If Not IsDBNull(v) Then
                    value = v
                End If
            Next row
        Catch ex As Exception
            'Service1.log("GetQueryValue: Exception: " & ex.Message)
        Finally
            conn.Close()

        End Try
        Service1.log("GetQueryValue: Value: " & value)
        Return value
    End Function
    Public Shared Function ExecuteConditionQuery(ByVal query As String) As Boolean

        Dim evaluated As Boolean = False
        Try

            Dim conn As New SqlConnection(ConfigurationManager.AppSettings.Get("DB"))
            conn.Open()
            Dim sql As String = query

            If sql.StartsWith("Select") = False And sql.StartsWith("SELECT") = False And sql.StartsWith("select") = False Then
                sql = "SELECT 1 WHERE " & sql
            End If
            'Service1.log("ExecuteConditionQuery: Query: " & sql)
            Try
                Dim a As SqlDataAdapter = New SqlDataAdapter(sql, conn)

                Dim dt As DataTable = New DataTable()
                a.Fill(dt)
                For Each r In dt.Rows
                    evaluated = True
                Next
            Catch ex As Exception
                'Service1.log("ExecuteConditionQuery: Exception: " & ex.Message)
            Finally
                conn.Close()
            End Try
        Catch ex As Exception
            'Service1.log("ExecuteConditionQuery: Exception: " & ex.Message)
        End Try


        Return evaluated
    End Function
    Public Shared Function GetQuery(ByVal source As String, ByVal column As String, ByVal table As String, ByVal reference As String) As String

        Dim query As String = ""

        If source = "profile" Then
            query = "SELECT data.[" & column & "] 
FROM VSAB.vsabuser.submission_pack_instances SPI 
left JOIN VSAB.vsabuser.submission_pack_versions SPV 
ON SPI.submission_pack_instance_id = SPV.submission_pack_instance_id  
left join VSAB.vsabuser.submission_pack_translation spt on spt.submission_pack_instance_id = spi.submission_pack_instance_id 
left JOIN  VSAB.vsabuser.entity_instance EI 
ON SPI.entity_instance_id = EI.entity_instance_id 
LEFT JOIN VSAB.VSABUSER.view_profile_data vpd
ON ei.entity_instance_id = vpd.entity_instance_id 
inner join [VSAB].[vsabuser].[" & table & "] data 
on data.owner_schema_instance_id =vpd.schema_instance_id
where spi.reference = '" & reference & "'"
        End If

        If source = "submission" Then
            query = "SELECT [" & column & "] 
FROM VSAB.vsabuser.submission_pack_instances SPI 
left JOIN VSAB.vsabuser.submission_pack_versions SPV 
ON SPI.submission_pack_instance_id = SPV.submission_pack_instance_id  
left join VSAB.vsabuser.submission_pack_translation spt on spt.submission_pack_instance_id = spi.submission_pack_instance_id 
inner JOIN  VSAB.vsabuser.entity_instance EI 
ON SPI.entity_instance_id = EI.entity_instance_id 
left join VSAB.vsabuser.entity_entity_groups eet  
on eet.entity_instance_id = ei.entity_instance_id 
left join VSAB.vsabuser.entity_group_translation egt  
on egt.group_id = eet.group_id 
inner JOIN VSAB.vsabuser.entity_instance_translation EIT 
ON SPI.entity_instance_id = EIT.entity_instance_id 
left JOIN VSAB.vsabuser.submission_pack_version_formset_instances SPFI 
ON SPV.submission_pack_version_id = SPFI.submission_pack_version_id  
left JOIN VSAB.vsabuser.formset_instances FI 
ON SPFI.formset_instance_id = FI.formset_instance_id 
left join VSAB.vsabuser.formset_details fd  
on fd.formset_id = fi.formset_guid and fd.version_number = (select top 1 version_number from VSAB.vsabuser.formset_details where formset_id = fd.formset_id order by version_number desc) 
left join VSAB.vsabuser.formset_details_translation fdt 
on fdt.formset_version_id = fd.formset_version_id  
left join VSAB.vsabuser.schema_instances si 
on si.schema_instance_id = fi.schema_instance_id 
inner join [VSAB].[vsabuser].[" & table & "] data 
on data.owner_schema_instance_id =fi.schema_instance_id
where spi.reference = '" & reference & "'"
        End If




        Return query
    End Function
End Class
