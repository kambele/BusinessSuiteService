IF COL_LENGTH('dbo.process_definitions', 'auto_assigning_from') IS NULL
BEGIN
    ALTER TABLE dbo.process_definitions ADD auto_assigning_from date NULL;
END;
GO

/*
    BusinessSuiteService application-loading queue repair

    A task can be recreated or repaired long after its originating application was
    submitted.  Queueing only by tasks.created_on keeps that task in the historical
    backlog even though the runtime row is new.  Include tasks.date_created in the
    recent-work priority so the service can pick the task up promptly.

    @BatchSize is retained for compatibility with existing service deployments,
    but the procedure intentionally returns every eligible unassigned task.  The
    workflow guards below still exclude future stages and processes with an active
    task.

    Before the queue is read, missing runtime task rows are repaired from the active
    task definitions.  The repair is idempotent per process/task-definition pair and
    uses update/range locks so overlapping service runs cannot insert the same task.

    A NULL process_definitions.auto_assigning_from applies no date restriction.  When
    configured, only processes submitted on or after that date are repaired or
    returned for automatic assignment.
*/
CREATE OR ALTER PROCEDURE dbo.GetUnassignedTaskList
    @BatchSize int = 250
AS
BEGIN
    SET NOCOUNT ON;

    IF @BatchSize IS NULL OR @BatchSize < 1
        SET @BatchSize = 250;

    DECLARE @now datetime = GETDATE();
    DECLARE @today date = CONVERT(date, @now);
    DECLARE @hour int = DATEPART(hour, @now);
    DECLARE @isWorkingTime bit = 0;

    IF @hour BETWEEN 8 AND 16
       AND @hour <> 13
       AND DATEDIFF(day, 0, @today) % 7 NOT IN (5, 6)
       AND NOT EXISTS
       (
           SELECT 1
           FROM dbo.public_holidays holiday
           WHERE holiday.holiday_date >= @today
             AND holiday.holiday_date < DATEADD(day, 1, @today)
       )
    BEGIN
        SET @isWorkingTime = 1;
    END;

    /*
        Repair processes whose insert completed without all of their runtime tasks.
        Determine the sequence across every applicable definition before filtering
        existing rows so a partially repaired process does not promote a later task
        to the first loading position.
    */
    BEGIN TRY
        BEGIN TRANSACTION;

        ;WITH applicable_definitions AS
        (
            SELECT process_row.id AS process_id,
                   process_row.activity_type,
                   process_row.form_action_date,
                   process_row.submitted_on,
                   process_row.created_on AS process_created_on,
                   process_row.date_created AS process_date_created,
                   process_row.form_due_date,
                   process_definition.loading_type,
                   task_definition.id AS task_definition_id,
                   task_definition.name AS task_name,
                   task_definition.role,
                   task_definition.sort_order,
                   ROW_NUMBER() OVER
                   (
                       PARTITION BY process_row.id
                       ORDER BY ISNULL(task_definition.sort_order, 2147483647), task_definition.id
                   ) AS runtime_sequence
            FROM dbo.processes process_row
            INNER JOIN dbo.process_types process_type
                ON process_type.id = process_row.activity_type
            INNER JOIN dbo.process_definitions process_definition
                ON process_definition.id = process_row.process_definition
            INNER JOIN dbo.task_definitions task_definition
                ON task_definition.process_definition = process_row.process_definition
            WHERE ISNULL(process_row.is_deleted, 0) = 0
              AND ISNULL(process_type.is_deleted, 0) = 0
              AND ISNULL(process_type.enable_auto_assign, 0) = 1
              AND ISNULL(process_definition.is_deleted, 0) = 0
              AND ISNULL(task_definition.is_deleted, 0) = 0
              AND ISNULL(process_row.statuss, 0) <> 4
              AND
              (
                  process_definition.auto_assigning_from IS NULL
                  OR process_row.submitted_on >= process_definition.auto_assigning_from
              )
              AND
              (
                  NULLIF(LTRIM(RTRIM(task_definition.process_types)), '') IS NULL
                  OR ',' + REPLACE(task_definition.process_types, ' ', '') + ','
                     LIKE '%,' + CONVERT(varchar(20), process_row.activity_type) + ',%'
              )
        )
        INSERT INTO dbo.tasks
        (
            task_definition,
            process,
            name,
            created_on,
            role,
            status,
            sort_order,
            force_auto_assign,
            enabled,
            is_deleted,
            date_created
        )
        SELECT definition.task_definition_id,
               definition.process_id,
               definition.task_name,
               CASE
                   WHEN definition.runtime_sequence <> 1 THEN NULL
                   WHEN UPPER(LTRIM(RTRIM(ISNULL(definition.loading_type, 'INSTANT')))) = 'AFTER DUE DATE'
                        AND definition.form_due_date IS NOT NULL
                       THEN dbo.DateAddWorkDay(1, definition.form_due_date)
                   ELSE COALESCE(definition.form_action_date,
                                 definition.submitted_on,
                                 definition.process_created_on,
                                 definition.process_date_created)
               END,
               definition.role,
               1,
               definition.sort_order,
               0,
               1,
               0,
               @now
        FROM applicable_definitions definition
        WHERE NOT EXISTS
        (
            SELECT 1
            FROM dbo.tasks existing_task WITH (UPDLOCK, HOLDLOCK)
            WHERE existing_task.process = definition.process_id
              AND existing_task.task_definition = definition.task_definition_id
              AND ISNULL(existing_task.is_deleted, 0) = 0
        );

        COMMIT TRANSACTION;
    END TRY
    BEGIN CATCH
        IF XACT_STATE() <> 0 ROLLBACK TRANSACTION;
        THROW;
    END CATCH;

    ;WITH open_tasks AS
    (
        SELECT task_row.id,
               task_row.process,
               task_row.force_auto_assign,
               task_row.date_created AS runtime_created_on,
               COALESCE(task_row.created_on,
                        process_row.form_action_date,
                        process_row.submitted_on,
                        process_row.created_on,
                        process_row.date_created) AS loading_date,
               ROW_NUMBER() OVER
               (
                   PARTITION BY task_row.process
                   ORDER BY ISNULL(task_row.sort_order, task_definition.sort_order), task_row.id
               ) AS open_sequence,
               task_definition.sort_order AS definition_sort_order,
               completion_definition.sort_order AS completion_sort_order,
               process_row.statuss AS process_status,
               process_row.analyst,
               process_row.external_status,
               process_row.submitted_on AS process_submitted_on,
               process_definition.auto_assigning_from
        FROM dbo.tasks task_row
        INNER JOIN dbo.task_definitions task_definition
            ON task_definition.id = task_row.task_definition
        INNER JOIN dbo.processes process_row
            ON process_row.id = task_row.process
        INNER JOIN dbo.process_types process_type
            ON process_type.id = process_row.activity_type
        INNER JOIN dbo.process_definitions process_definition
            ON process_definition.id = process_row.process_definition
        LEFT JOIN dbo.task_definitions completion_definition
            ON completion_definition.id = process_type.completion_task
        WHERE task_row.status = 1
          AND task_row.assigned_to IS NULL
          AND task_row.is_deleted = 0
          AND ISNULL(task_row.enabled, 1) = 1
          AND process_row.is_deleted = 0
          AND process_type.is_deleted = 0
          AND ISNULL(process_type.enable_auto_assign, 0) = 1
          AND task_row.date_created NOT BETWEEN '20250224 23:50:00' AND '20250225 00:15:00'
    ),
    eligible_tasks AS
    (
        SELECT task_row.*
        FROM open_tasks task_row
        WHERE task_row.loading_date IS NOT NULL
          AND task_row.loading_date <= @now
          AND
          (
              task_row.auto_assigning_from IS NULL
              OR task_row.process_submitted_on >= task_row.auto_assigning_from
          )
          AND NOT EXISTS
          (
              SELECT 1
              FROM dbo.tasks active_task
              WHERE active_task.process = task_row.process
                AND active_task.status = 2
                AND active_task.is_deleted = 0
          )
          AND
          (
              ISNULL(task_row.force_auto_assign, 0) = 1
              OR
              (
                  @isWorkingTime = 1
                  AND task_row.open_sequence = 1
                  AND task_row.process_status <> 4
                  AND
                  (
                      task_row.external_status IN ('Pending Approval', 'Resubmission Requested')
                      OR ISNULL(task_row.analyst, 0) > 0
                  )
                  AND task_row.definition_sort_order <= task_row.completion_sort_order
              )
          )
    )
    SELECT task_row.id
    FROM eligible_tasks task_row
    ORDER BY ISNULL(task_row.force_auto_assign, 0) DESC,
             CASE
                 WHEN task_row.runtime_created_on >= DATEADD(day, -2, @now)
                   OR task_row.loading_date >= DATEADD(day, -2, @now)
                 THEN 0 ELSE 1
             END,
             CASE
                 WHEN task_row.runtime_created_on >= DATEADD(day, -2, @now)
                 THEN task_row.runtime_created_on
                 WHEN task_row.loading_date >= DATEADD(day, -2, @now)
                 THEN task_row.loading_date
             END DESC,
             task_row.loading_date,
             task_row.id;
END;
GO

