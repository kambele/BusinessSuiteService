/*
    Fix: corporate broker Reinsurance Exemption submissions are not loaded.

    Production run order:
      1. Stop/pause BusinessSuiteService and any BusinessSuiteConsole loader.
      2. Run this script against BusinessSuite.
      3. Start the service and allow one Update cycle to load the backlog.
      4. Run the verification queries at the end of this script.

    The script is idempotent. It does not insert missing dbo.processes rows
    directly because PostAjax must create their workflow tasks and related data.
*/

USE [BusinessSuite];
GO

SET ANSI_NULLS ON;
GO
SET QUOTED_IDENTIFIER ON;
GO

/*
    Return one current version per submission pack and one current entity group.
    @endDate is an exclusive upper bound; Service1 passes tomorrow's date.
*/
ALTER PROCEDURE [dbo].[GetBusinessSuiteSubmissionPacks2]
    @startDate date,
    @endDate date,
    @formset varchar(max),
    @group bigint
AS
BEGIN
    SET NOCOUNT ON;

    DECLARE @guid uniqueidentifier = TRY_CONVERT(uniqueidentifier, @formset);

    IF @guid IS NULL
        THROW 50001, 'GetBusinessSuiteSubmissionPacks2 received an invalid formset GUID.', 1;

    ;WITH RankedVersions AS
    (
        SELECT spv.*,
               ROW_NUMBER() OVER
               (
                   PARTITION BY spv.submission_pack_instance_id
                   ORDER BY spv.version_no_major DESC,
                            spv.version_no_minor DESC,
                            spv.submission_pack_version_id DESC
               ) AS version_rank
        FROM VSAB.vsabuser.submission_pack_versions AS spv
    )
    SELECT ei.entity_instance_id,
           current_group.group_id,
           spv.state_last_changed_on,
           spi.reference,
           pack_name.submission_pack_name,
           spi.end_date,
           spi.created_on,
           spv.action_performed,
           spv.submitted_on
    FROM VSAB.vsabuser.submission_pack_instances AS spi
    INNER JOIN RankedVersions AS spv
        ON spv.submission_pack_instance_id = spi.submission_pack_instance_id
       AND spv.version_rank = 1
    INNER JOIN VSAB.vsabuser.entity_instance AS ei
        ON ei.entity_instance_id = spi.entity_instance_id
    OUTER APPLY
    (
        SELECT TOP (1) eeg.group_id
        FROM VSAB.vsabuser.entity_entity_groups AS eeg
        WHERE eeg.entity_instance_id = ei.entity_instance_id
          AND eeg.end_date IS NULL
        ORDER BY eeg.start_date DESC, eeg.Id DESC
    ) AS current_group
    OUTER APPLY
    (
        SELECT TOP (1) spt.submission_pack_name
        FROM VSAB.vsabuser.submission_pack_translation AS spt
        WHERE spt.submission_pack_instance_id = spi.submission_pack_instance_id
        ORDER BY CASE spt.language
                     WHEN 'en-GB' THEN 0
                     WHEN 'en' THEN 1
                     ELSE 2
                 END,
                 spt.translation_id
    ) AS pack_name
    WHERE spv.submitted_on >= @startDate
      AND spv.submitted_on < @endDate
      AND spi.is_deleted = 0
      AND EXISTS
          (
              SELECT 1
              FROM VSAB.vsabuser.submission_pack_version_formset_instances AS spfi
              INNER JOIN VSAB.vsabuser.formset_instances AS fi
                  ON fi.formset_instance_id = spfi.formset_instance_id
              WHERE spfi.submission_pack_version_id = spv.submission_pack_version_id
                AND fi.formset_guid = @guid
                AND fi.is_deleted = 0
          )
      AND (current_group.group_id = @group OR ei.entity_type_id = 2)
      AND
          (
              (spv.action_performed = 'SubmitReturnForApproval' AND spv.state_id = 9)
              OR (spv.action_performed = 'ApproveSubmissionPack' AND spv.state_id = 3)
              OR (spv.action_performed = 'SubmitReturn' AND spv.state_id = 9)
          );
END;
GO

/*
    The targeted console procedure previously ignored @formset and @group.
    Keep its behavior aligned with the service procedure so a reference cannot
    be manually loaded under an unrelated process type.
*/
ALTER PROCEDURE [dbo].[GetBusinessSuiteSubmissionPacksSingle]
    @formset varchar(max),
    @group bigint,
    @reference varchar(max)
AS
BEGIN
    SET NOCOUNT ON;

    DECLARE @guid uniqueidentifier = TRY_CONVERT(uniqueidentifier, @formset);

    IF @guid IS NULL
        THROW 50002, 'GetBusinessSuiteSubmissionPacksSingle received an invalid formset GUID.', 1;

    ;WITH RankedVersions AS
    (
        SELECT spv.*,
               ROW_NUMBER() OVER
               (
                   PARTITION BY spv.submission_pack_instance_id
                   ORDER BY spv.version_no_major DESC,
                            spv.version_no_minor DESC,
                            spv.submission_pack_version_id DESC
               ) AS version_rank
        FROM VSAB.vsabuser.submission_pack_versions AS spv
        INNER JOIN VSAB.vsabuser.submission_pack_instances AS filter_spi
            ON filter_spi.submission_pack_instance_id = spv.submission_pack_instance_id
        WHERE filter_spi.reference = @reference
    )
    SELECT ei.entity_instance_id,
           current_group.group_id,
           spv.state_last_changed_on,
           spi.reference,
           pack_name.submission_pack_name,
           spi.end_date,
           spi.created_on,
           spv.action_performed,
           spv.submitted_on
    FROM VSAB.vsabuser.submission_pack_instances AS spi
    INNER JOIN RankedVersions AS spv
        ON spv.submission_pack_instance_id = spi.submission_pack_instance_id
       AND spv.version_rank = 1
    INNER JOIN VSAB.vsabuser.entity_instance AS ei
        ON ei.entity_instance_id = spi.entity_instance_id
    OUTER APPLY
    (
        SELECT TOP (1) eeg.group_id
        FROM VSAB.vsabuser.entity_entity_groups AS eeg
        WHERE eeg.entity_instance_id = ei.entity_instance_id
          AND eeg.end_date IS NULL
        ORDER BY eeg.start_date DESC, eeg.Id DESC
    ) AS current_group
    OUTER APPLY
    (
        SELECT TOP (1) spt.submission_pack_name
        FROM VSAB.vsabuser.submission_pack_translation AS spt
        WHERE spt.submission_pack_instance_id = spi.submission_pack_instance_id
        ORDER BY CASE spt.language
                     WHEN 'en-GB' THEN 0
                     WHEN 'en' THEN 1
                     ELSE 2
                 END,
                 spt.translation_id
    ) AS pack_name
    WHERE spi.reference = @reference
      AND spi.is_deleted = 0
      AND EXISTS
          (
              SELECT 1
              FROM VSAB.vsabuser.submission_pack_version_formset_instances AS spfi
              INNER JOIN VSAB.vsabuser.formset_instances AS fi
                  ON fi.formset_instance_id = spfi.formset_instance_id
              WHERE spfi.submission_pack_version_id = spv.submission_pack_version_id
                AND fi.formset_guid = @guid
                AND fi.is_deleted = 0
          )
      AND (current_group.group_id = @group OR ei.entity_type_id = 2)
      AND
          (
              (spv.action_performed = 'SubmitReturnForApproval' AND spv.state_id = 9)
              OR (spv.action_performed = 'ApproveSubmissionPack' AND spv.state_id = 3)
              OR (spv.action_performed = 'SubmitReturn' AND spv.state_id = 9)
          );
END;
GO

SET XACT_ABORT ON;
SET TRANSACTION ISOLATION LEVEL SERIALIZABLE;

BEGIN TRY
    BEGIN TRANSACTION;

    DECLARE @FormsetGuid uniqueidentifier = 'B3EF0654-5303-4938-ACD7-D05FF4A5FA57';
    DECLARE @ProcessTypeName varchar(max) = 'Application for Reinsurance Exemption - Corporate Short-term Insurance Broker';
    DECLARE @FormTypeId bigint;
    DECLARE @EntityTypeId bigint;
    DECLARE @TargetProcessTypeId bigint;
    DECLARE @TemplateProcessTypeId bigint = 227;
    DECLARE @ReusableProcessTypeId bigint = 244;
    DECLARE @AttributeModuleId bigint;
    DECLARE @AttributeTable sysname;

    SELECT @FormTypeId = ft.id
    FROM dbo.form_types AS ft WITH (UPDLOCK, HOLDLOCK)
    WHERE TRY_CONVERT(uniqueidentifier, ft.form_type_id) = @FormsetGuid
      AND ft.is_deleted = 0;

    IF @FormTypeId IS NULL
        THROW 50003, 'The Reinsurance Exemption form type is missing or deleted.', 1;

    SELECT @EntityTypeId = et.id
    FROM dbo.entity_types AS et WITH (UPDLOCK, HOLDLOCK)
    WHERE et.id = 32
      AND et.entity_group_id = 28
      AND et.is_deleted = 0;

    IF @EntityTypeId IS NULL
        THROW 50004, 'Business Suite entity type 32 is not mapped to VSAB group 28.', 1;

    IF NOT EXISTS
       (
           SELECT 1
           FROM dbo.process_types
           WHERE id = @TemplateProcessTypeId
             AND process_definition = 20033
             AND entity_group = @EntityTypeId
             AND is_deleted = 0
       )
        THROW 50005, 'Process type 227 is not a valid corporate short-term template.', 1;

    SELECT @TargetProcessTypeId = pt.id
    FROM dbo.process_types AS pt WITH (UPDLOCK, HOLDLOCK)
    WHERE pt.form_type = @FormTypeId
      AND pt.process_definition = 20033
      AND pt.entity_group = @EntityTypeId
      AND pt.is_deleted = 0;

    IF @TargetProcessTypeId IS NULL
       AND EXISTS
       (
           SELECT 1
           FROM dbo.process_types AS pt
           WHERE pt.id = @ReusableProcessTypeId
             AND pt.process_definition = 20033
             AND pt.entity_group = @EntityTypeId
             AND pt.is_deleted = 1
             AND NOT EXISTS
                 (
                     SELECT 1
                     FROM dbo.processes AS p
                     WHERE p.activity_type = pt.id
                 )
       )
    BEGIN
        UPDATE dbo.process_types
        SET name = @ProcessTypeName,
            description = @ProcessTypeName,
            form_type = @FormTypeId,
            is_deleted = 0,
            date_updated = GETDATE()
        WHERE id = @ReusableProcessTypeId;

        SET @TargetProcessTypeId = @ReusableProcessTypeId;

        SELECT @AttributeModuleId = attributes
        FROM dbo.process_types
        WHERE id = @TargetProcessTypeId;

        UPDATE dbo.modules
        SET name = 'Process Types - ' + @ProcessTypeName,
            page_title = @ProcessTypeName,
            is_active = 1,
            is_show = 1,
            is_deleted = 0
        WHERE id = @AttributeModuleId;
    END;

    IF @TargetProcessTypeId IS NULL
    BEGIN
        INSERT dbo.process_types
        (
            name, description, slc, division, attributes, form_type,
            process_definition, entity_group, completion_task,
            assigning_role, supervisory_role, enable_auto_assign,
            condition, assigning_type, note, child_module, child_data,
            is_deleted, date_created, created_by
        )
        SELECT @ProcessTypeName, @ProcessTypeName, template.slc,
               template.division, NULL, @FormTypeId,
               template.process_definition, @EntityTypeId,
               template.completion_task, template.assigning_role,
               template.supervisory_role, template.enable_auto_assign,
               template.condition, template.assigning_type,
               'Created by 20260910 Reinsurance Exemption loading rectification.',
               template.child_module, template.child_data, 0, GETDATE(), NULL
        FROM dbo.process_types AS template
        WHERE template.id = @TemplateProcessTypeId;

        SET @TargetProcessTypeId = CONVERT(bigint, SCOPE_IDENTITY());
        SET @AttributeTable = CONVERT(sysname, 'sub_module_71404_' + CONVERT(varchar(20), @TargetProcessTypeId));

        INSERT dbo.modules
        (
            parent_module_id, name, module_type_id, page_title, page_subtitle,
            slug, icon, is_active, sort_order, description, database_table,
            alias, is_show, permissions, version, is_deleted,
            insert_email, update_email, delete_email, url, data_source
        )
        VALUES
        (
            30073, 'Process Types - ' + @ProcessTypeName, 4, @ProcessTypeName, NULL,
            @AttributeTable, NULL, 1, 0, NULL, @AttributeTable,
            '', 1, '', 0, 0,
            NULL, NULL, NULL, NULL, NULL
        );

        SET @AttributeModuleId = CONVERT(bigint, SCOPE_IDENTITY());

        UPDATE dbo.process_types
        SET attributes = @AttributeModuleId
        WHERE id = @TargetProcessTypeId;

        IF OBJECT_ID('dbo.' + @AttributeTable, 'U') IS NULL
        BEGIN
            DECLARE @CreateAttributeTableSql nvarchar(max) =
                N'CREATE TABLE dbo.' + QUOTENAME(@AttributeTable) + N'(
                    [id] bigint IDENTITY(1,1) NOT NULL,
                    [module] bigint NULL,
                    [data] bigint NULL,
                    [note] varchar(max) NULL,
                    [is_deleted] bit NOT NULL CONSTRAINT ' + QUOTENAME('DF_' + @AttributeTable + '_is_deleted') + N' DEFAULT ((0)),
                    [date_created] datetime NOT NULL CONSTRAINT ' + QUOTENAME('DF_' + @AttributeTable + '_date_created') + N' DEFAULT (GETDATE()),
                    [created_by] bigint NULL,
                    [date_updated] datetime NULL,
                    [updated_by] bigint NULL,
                    CONSTRAINT ' + QUOTENAME('PK_' + @AttributeTable) + N' PRIMARY KEY CLUSTERED ([id] ASC)
                );';

            EXEC sys.sp_executesql @CreateAttributeTableSql;
        END;
    END;

    /*
        STARE9822979 was loaded through the old unfiltered single-reference
        procedure as activity type 227 (Dispensation). Move it to the new
        Exemption process type in place so its existing tasks remain attached.
    */
    IF EXISTS
       (
           SELECT 1
           FROM dbo.processes AS wrong_process
           WHERE wrong_process.activity_type = @TemplateProcessTypeId
             AND wrong_process.is_deleted = 0
             AND EXISTS
                 (
                     SELECT 1
                     FROM dbo.processes AS correct_process
                     WHERE correct_process.external_reference = wrong_process.external_reference
                       AND correct_process.activity_type = @TargetProcessTypeId
                       AND correct_process.is_deleted = 0
                 )
             AND EXISTS
                 (
                     SELECT 1
                     FROM VSAB.vsabuser.submission_pack_instances AS spi
                     INNER JOIN VSAB.vsabuser.submission_pack_versions AS spv
                         ON spv.submission_pack_instance_id = spi.submission_pack_instance_id
                     INNER JOIN VSAB.vsabuser.submission_pack_version_formset_instances AS spfi
                         ON spfi.submission_pack_version_id = spv.submission_pack_version_id
                     INNER JOIN VSAB.vsabuser.formset_instances AS fi
                         ON fi.formset_instance_id = spfi.formset_instance_id
                     WHERE spi.reference = wrong_process.external_reference
                       AND fi.formset_guid = @FormsetGuid
                 )
       )
        THROW 50006, 'A reference has both the incorrect and correct active process types; resolve the duplicate before continuing.', 1;

    UPDATE wrong_process
    SET activity_type = @TargetProcessTypeId,
        date_updated = GETDATE()
    FROM dbo.processes AS wrong_process
    WHERE wrong_process.activity_type = @TemplateProcessTypeId
      AND wrong_process.is_deleted = 0
      AND wrong_process.process_definition = 20033
      AND wrong_process.entity_type = @EntityTypeId
      AND EXISTS
          (
              SELECT 1
              FROM VSAB.vsabuser.submission_pack_instances AS spi
              INNER JOIN VSAB.vsabuser.submission_pack_versions AS spv
                  ON spv.submission_pack_instance_id = spi.submission_pack_instance_id
              INNER JOIN VSAB.vsabuser.submission_pack_version_formset_instances AS spfi
                  ON spfi.submission_pack_version_id = spv.submission_pack_version_id
              INNER JOIN VSAB.vsabuser.formset_instances AS fi
                  ON fi.formset_instance_id = spfi.formset_instance_id
              WHERE spi.reference = wrong_process.external_reference
                AND fi.formset_guid = @FormsetGuid
          );

    COMMIT TRANSACTION;

    SELECT @TargetProcessTypeId AS corporate_short_term_reinsurance_exemption_process_type_id;
END TRY
BEGIN CATCH
    IF @@TRANCOUNT > 0
        ROLLBACK TRANSACTION;

    THROW;
END CATCH;
GO

/* Verification: the service should create the missing processes on its next cycle. */
SELECT pt.id,
       pt.name,
       pt.form_type,
       ft.form_name,
       pt.process_definition,
       pt.entity_group,
       et.name AS entity_type_name,
       et.entity_group_id AS vsab_group_id,
       pt.attributes,
       pt.is_deleted
FROM dbo.process_types AS pt
INNER JOIN dbo.form_types AS ft ON ft.id = pt.form_type
INNER JOIN dbo.entity_types AS et ON et.id = pt.entity_group
WHERE TRY_CONVERT(uniqueidentifier, ft.form_type_id) = 'B3EF0654-5303-4938-ACD7-D05FF4A5FA57'
  AND et.entity_group_id = 28;

SELECT expected.reference,
       p.id AS process_id,
       p.activity_type,
       pt.name AS process_type,
       p.is_deleted,
       p.date_created
FROM
(
    VALUES
        ('STARE11081689'),
        ('STARE10912809'),
        ('STARE10912805'),
        ('STARE10912801'),
        ('STARE9450911'),
        ('STARE10656793'),
        ('STARE10656794'),
        ('STARE9822979')
) AS expected(reference)
LEFT JOIN dbo.processes AS p
    ON p.external_reference = expected.reference
   AND p.is_deleted = 0
LEFT JOIN dbo.process_types AS pt
    ON pt.id = p.activity_type
ORDER BY expected.reference;
GO
