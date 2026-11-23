/*
    BusinessSuiteService application-loading queue repair

    A task can be recreated or repaired long after its originating application was
    submitted.  Queueing only by tasks.created_on keeps that task in the historical
    backlog even though the runtime row is new.  Include tasks.date_created in the
    recent-work priority so the service can pick the task up promptly.
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
    DECLARE @rotationOffset bigint =
        CONVERT(bigint, DATEDIFF(minute, CONVERT(datetime, '20200101', 112), @now) / 2) * @BatchSize;

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
    ),
    eligible_tasks AS
    (
        SELECT task_row.*
        FROM open_tasks task_row
        WHERE task_row.loading_date IS NOT NULL
          AND task_row.loading_date <= @now
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
    ),
    numbered_tasks AS
    (
        SELECT task_row.*,
               ROW_NUMBER() OVER
               (
                   ORDER BY ISNULL(task_row.force_auto_assign, 0) DESC,
                            task_row.loading_date,
                            task_row.id
               ) AS queue_number,
               COUNT_BIG(*) OVER () AS queue_count
        FROM eligible_tasks task_row
    )
    SELECT TOP (@BatchSize) task_row.id
    FROM numbered_tasks task_row
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
             CASE
                 WHEN task_row.queue_count = 0 THEN 0
                 ELSE
                     (
                         CONVERT(bigint, task_row.queue_number - 1)
                         - (@rotationOffset % task_row.queue_count)
                         + task_row.queue_count
                     ) % task_row.queue_count
             END,
             task_row.id;
END;
GO

