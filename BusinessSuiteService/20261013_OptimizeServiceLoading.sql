USE [BusinessSuite];
GO

SET NOCOUNT ON;
SET XACT_ABORT ON;
SET ANSI_NULLS ON;
SET QUOTED_IDENTIFIER ON;
GO

BEGIN TRANSACTION;

UPDATE dbo.process_definitions
SET loading_type = 'INSTANT'
WHERE loading_type IS NULL
   OR UPPER(LTRIM(RTRIM(loading_type))) NOT IN ('INSTANT', 'AFTER DUE DATE');

IF NOT EXISTS
(
    SELECT 1
    FROM sys.indexes
    WHERE object_id = OBJECT_ID(N'dbo.tasks')
      AND name = N'IX_tasks_AutoAssignQueue'
)
BEGIN
    CREATE NONCLUSTERED INDEX IX_tasks_AutoAssignQueue
        ON dbo.tasks ([status], is_deleted, assigned_to, process, sort_order, created_on)
        INCLUDE (task_definition, force_auto_assign, enabled, completion_date, date_created);
END;

IF NOT EXISTS
(
    SELECT 1
    FROM sys.indexes
    WHERE object_id = OBJECT_ID(N'dbo.tasks')
      AND name = N'IX_tasks_OpenUnassigned'
)
BEGIN
    CREATE NONCLUSTERED INDEX IX_tasks_OpenUnassigned
        ON dbo.tasks (process, sort_order, created_on)
        INCLUDE (task_definition, force_auto_assign, enabled, completion_date, date_created)
        WHERE status = 1 AND assigned_to IS NULL AND is_deleted = 0;
END;

IF NOT EXISTS
(
    SELECT 1
    FROM sys.indexes
    WHERE object_id = OBJECT_ID(N'dbo.tasks')
      AND name = N'IX_tasks_ActiveProcess'
)
BEGIN
    CREATE NONCLUSTERED INDEX IX_tasks_ActiveProcess
        ON dbo.tasks (process)
        WHERE status = 2 AND is_deleted = 0;
END;

IF NOT EXISTS
(
    SELECT 1
    FROM sys.indexes
    WHERE object_id = OBJECT_ID(N'dbo.tasks')
      AND name = N'IX_tasks_ProcessStatus'
)
BEGIN
    CREATE NONCLUSTERED INDEX IX_tasks_ProcessStatus
        ON dbo.tasks (process, [status], sort_order)
        INCLUDE (assigned_to, task_definition, is_deleted, enabled, force_auto_assign, created_on);
END;

IF NOT EXISTS
(
    SELECT 1
    FROM sys.indexes
    WHERE object_id = OBJECT_ID(N'dbo.public_holidays')
      AND name = N'IX_public_holidays_HolidayDate'
)
BEGIN
    CREATE NONCLUSTERED INDEX IX_public_holidays_HolidayDate
        ON dbo.public_holidays (holiday_date);
END;

COMMIT TRANSACTION;
GO

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

    ;WITH open_tasks AS
    (
        SELECT task_row.id,
               task_row.process,
               task_row.task_definition,
               task_row.force_auto_assign,
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
               process_row.external_status
        FROM dbo.tasks task_row
        INNER JOIN dbo.task_definitions task_definition
            ON task_definition.id = task_row.task_definition
        INNER JOIN dbo.processes process_row
            ON process_row.id = task_row.process
        INNER JOIN dbo.process_types process_type
            ON process_type.id = process_row.activity_type
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
    )
    SELECT TOP (@BatchSize) task_row.id
    FROM open_tasks task_row
    WHERE task_row.loading_date IS NOT NULL
      AND task_row.loading_date <= @now
      AND
      (
          (
              ISNULL(task_row.force_auto_assign, 0) = 1
              AND NOT EXISTS
              (
                  SELECT 1
                  FROM dbo.tasks active_task
                  WHERE active_task.process = task_row.process
                    AND active_task.status = 2
                    AND active_task.is_deleted = 0
              )
          )
          OR
          (
              @isWorkingTime = 1
              AND task_row.open_sequence = 1
              AND task_row.process_status <> 4
              AND
              (
                  task_row.external_status IN ('Pending Approval', 'Resubmission Requested')
                  OR task_row.analyst IS NOT NULL
              )
              AND task_row.definition_sort_order <= task_row.completion_sort_order
          )
      )
    ORDER BY ISNULL(task_row.force_auto_assign, 0) DESC,
             task_row.loading_date,
             task_row.id;
END;
GO

CREATE OR ALTER TRIGGER dbo.TR_tasks_apply_process_loading_type
ON dbo.tasks
AFTER INSERT
AS
BEGIN
    SET NOCOUNT ON;

    UPDATE task_row
    SET created_on = CASE
        WHEN UPPER(LTRIM(RTRIM(ISNULL(definition.loading_type, 'INSTANT')))) = 'AFTER DUE DATE'
             AND process_row.form_due_date IS NOT NULL
            THEN dbo.DateAddWorkDay(1, process_row.form_due_date)
        ELSE COALESCE(process_row.form_action_date,
                      process_row.submitted_on,
                      process_row.created_on,
                      process_row.date_created,
                      GETDATE())
    END
    FROM dbo.tasks task_row
    INNER JOIN inserted inserted_row ON inserted_row.id = task_row.id
    INNER JOIN dbo.processes process_row ON process_row.id = task_row.process
    INNER JOIN dbo.process_definitions definition ON definition.id = process_row.process_definition
    LEFT JOIN dbo.task_definitions task_definition ON task_definition.id = task_row.task_definition
    WHERE task_row.assigned_to IS NULL
      AND task_row.completion_date IS NULL
      AND ISNULL(task_row.sort_order, task_definition.sort_order) = 1;
END;
GO

CREATE OR ALTER TRIGGER dbo.TR_processes_refresh_loading_date
ON dbo.processes
AFTER UPDATE
AS
BEGIN
    SET NOCOUNT ON;

    IF NOT (UPDATE(form_due_date) OR UPDATE(form_action_date) OR UPDATE(form_status_change)
            OR UPDATE(submitted_on) OR UPDATE(process_definition))
        RETURN;

    UPDATE task_row
    SET created_on = CASE
        WHEN UPPER(LTRIM(RTRIM(ISNULL(definition.loading_type, 'INSTANT')))) = 'AFTER DUE DATE'
             AND process_row.form_due_date IS NOT NULL
            THEN dbo.DateAddWorkDay(1, process_row.form_due_date)
        ELSE COALESCE(process_row.form_action_date,
                      process_row.submitted_on,
                      process_row.created_on,
                      process_row.date_created,
                      GETDATE())
    END
    FROM dbo.tasks task_row
    INNER JOIN inserted process_row ON process_row.id = task_row.process
    INNER JOIN dbo.process_definitions definition ON definition.id = process_row.process_definition
    LEFT JOIN dbo.task_definitions task_definition ON task_definition.id = task_row.task_definition
    WHERE task_row.assigned_to IS NULL
      AND task_row.completion_date IS NULL
      AND ISNULL(task_row.status, 1) = 1
      AND ISNULL(task_row.sort_order, task_definition.sort_order) = 1;
END;
GO

CREATE OR ALTER TRIGGER dbo.TR_process_definitions_refresh_loading_date
ON dbo.process_definitions
AFTER UPDATE
AS
BEGIN
    SET NOCOUNT ON;

    IF NOT UPDATE(loading_type)
        RETURN;

    UPDATE task_row
    SET created_on = CASE
        WHEN UPPER(LTRIM(RTRIM(ISNULL(definition.loading_type, 'INSTANT')))) = 'AFTER DUE DATE'
             AND process_row.form_due_date IS NOT NULL
            THEN dbo.DateAddWorkDay(1, process_row.form_due_date)
        ELSE COALESCE(process_row.form_action_date,
                      process_row.submitted_on,
                      process_row.created_on,
                      process_row.date_created,
                      GETDATE())
    END
    FROM dbo.tasks task_row
    INNER JOIN dbo.processes process_row ON process_row.id = task_row.process
    INNER JOIN inserted definition ON definition.id = process_row.process_definition
    LEFT JOIN dbo.task_definitions task_definition ON task_definition.id = task_row.task_definition
    WHERE task_row.assigned_to IS NULL
      AND task_row.completion_date IS NULL
      AND ISNULL(task_row.status, 1) = 1
      AND ISNULL(task_row.sort_order, task_definition.sort_order) = 1;
END;
GO
