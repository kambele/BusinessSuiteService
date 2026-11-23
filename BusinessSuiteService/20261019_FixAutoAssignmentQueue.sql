USE [BusinessSuite];
GO

SET NOCOUNT ON;
SET XACT_ABORT ON;
GO

/*
    Preserve the role-cluster values changed by this deployment.  The backup
    table makes the targeted configuration repair independently reversible.
*/
IF OBJECT_ID(N'dbo.role_clusters_backup_20261019', N'U') IS NULL
BEGIN
    SELECT *
    INTO dbo.role_clusters_backup_20261019
    FROM dbo.role_clusters
    WHERE id IN (6, 15, 16, 30);
END;
GO

IF OBJECT_ID(N'dbo.autoassign_procedure_backup_20261019', N'U') IS NULL
BEGIN
    SELECT procedure_object.name AS procedure_name,
           procedure_module.definition,
           procedure_object.create_date,
           procedure_object.modify_date,
           GETDATE() AS backed_up_at
    INTO dbo.autoassign_procedure_backup_20261019
    FROM sys.procedures procedure_object
    INNER JOIN sys.sql_modules procedure_module
        ON procedure_module.object_id = procedure_object.object_id
    WHERE procedure_object.name IN
          (
              N'GetUnassignedTaskList',
              N'GetNextTaskDefinitionAgent'
          );
END;
GO

IF OBJECT_ID(N'dbo.processes_analyst_backup_20261019', N'U') IS NULL
BEGIN
    SELECT *
    INTO dbo.processes_analyst_backup_20261019
    FROM dbo.processes
    WHERE id IN (214668, 214669);
END;
GO

UPDATE dbo.role_clusters
SET end_date = NULL,
    date_updated = GETDATE()
WHERE id IN (6, 15, 16, 30)
  AND is_deleted = 0
  AND end_date IS NOT NULL
  AND end_date < CONVERT(date, GETDATE());
GO

UPDATE dbo.processes
SET analyst = assigned_to,
    date_updated = GETDATE()
WHERE id IN (214668, 214669)
  AND ISNULL(analyst, 0) = 0
  AND assigned_to IS NOT NULL;
GO

/*
    Accept both the runtime task id (the canonical contract) and the legacy
    task-definition id used by older deployed service builds.  The latter
    returns role candidates; the calling service still intersects the result
    with the current application's matching role cluster.
*/
CREATE OR ALTER PROCEDURE dbo.GetNextTaskDefinitionAgent
    @task_definition bigint
AS
BEGIN
    SET NOCOUNT ON;

    DECLARE @input bigint = @task_definition;
    DECLARE @task bigint = NULL;
    DECLARE @role bigint = NULL;
    DECLARE @entityType bigint = NULL;
    DECLARE @reference varchar(max) = NULL;
    DECLARE @process bigint = NULL;
    DECLARE @definitionRole bigint = NULL;
    DECLARE @legacyDefinitionInput bit = 0;

    SELECT
        @task_definition = task_row.task_definition,
        @task = task_row.id,
        @role = task_row.role
    FROM dbo.tasks task_row
    WHERE task_row.id = @input
      AND task_row.is_deleted = 0;

    IF @task IS NULL
       AND EXISTS
       (
           SELECT 1
           FROM dbo.task_definitions definition
           WHERE definition.id = @input
             AND definition.is_deleted = 0
       )
    BEGIN
        SET @task_definition = @input;
        SET @legacyDefinitionInput = 1;
    END;

    SELECT @definitionRole = definition.role
    FROM dbo.task_definitions definition
    WHERE definition.id = @task_definition
      AND definition.is_deleted = 0;

    IF @definitionRole IS NULL
        RETURN;

    IF @task IS NOT NULL
    BEGIN
        SELECT
            @process = process_row.id,
            @reference = process_row.external_reference,
            @entityType = process_row.entity_type
        FROM dbo.processes process_row
        INNER JOIN dbo.tasks task_row
            ON task_row.process = process_row.id
           AND task_row.id = @task
           AND task_row.is_deleted = 0
        WHERE process_row.is_deleted = 0;

        IF EXISTS
        (
            SELECT 1
            FROM dbo.processes previous_process
            WHERE previous_process.is_deleted = 0
              AND previous_process.statuss = 2
              AND previous_process.assigned_to IS NOT NULL
              AND previous_process.external_reference = @reference
        )
        BEGIN
            SELECT previous_task.assigned_to
            FROM dbo.tasks previous_task
            INNER JOIN dbo.processes previous_process
                ON previous_process.id = previous_task.process
               AND previous_process.is_deleted = 0
            WHERE previous_process.external_reference = @reference
              AND previous_process.statuss = 2
              AND previous_process.assigned_to IS NOT NULL
              AND previous_task.is_deleted = 0
              AND previous_task.sort_order = 1
              AND previous_task.assigned_to IS NOT NULL
            GROUP BY previous_task.assigned_to;
            RETURN;
        END;
    END;

    SELECT candidate.id
    FROM dbo.users candidate
    WHERE candidate.is_deleted = 0
      AND @definitionRole IN (SELECT * FROM dbo.fn_split(candidate.roles, ','))
      AND EXISTS
      (
          SELECT 1
          FROM dbo.role_clusters cluster
          WHERE cluster.is_deleted = 0
            AND cluster.role = COALESCE(@role, @definitionRole)
            AND candidate.id IN (SELECT * FROM dbo.fn_split(cluster.cluster_users, ','))
            AND
            (
                @legacyDefinitionInput = 1
                OR @entityType IN (SELECT * FROM dbo.fn_split(cluster.entity_groups, ','))
            )
      )
    ORDER BY COALESCE
    (
        (
            SELECT MAX(assigned_task.date_assigned)
            FROM dbo.tasks assigned_task
            WHERE assigned_task.assigned_to = candidate.id
              AND assigned_task.is_deleted = 0
        ),
        CONVERT(datetime, '19000101', 112)
    ), candidate.id;
END;
GO

/*
    Only expose a true next task when the process has no active task.  Recent
    submissions are prioritised while the remaining backlog is rotated in
    deterministic two-minute windows so an unassignable legacy record cannot
    permanently block the queue.
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
             CASE WHEN task_row.loading_date >= DATEADD(day, -2, @now) THEN 0 ELSE 1 END,
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

/* Keep the process-level analyst in step with its first assigned task. */
CREATE OR ALTER TRIGGER dbo.TR_tasks_sync_process_analyst
ON dbo.tasks
AFTER INSERT, UPDATE
AS
BEGIN
    SET NOCOUNT ON;

    UPDATE process_row
    SET analyst = inserted_task.assigned_to,
        date_updated = GETDATE()
    FROM dbo.processes process_row
    INNER JOIN inserted inserted_task
        ON inserted_task.process = process_row.id
    LEFT JOIN dbo.task_definitions definition
        ON definition.id = inserted_task.task_definition
    WHERE inserted_task.assigned_to IS NOT NULL
      AND inserted_task.is_deleted = 0
      AND ISNULL(inserted_task.sort_order, definition.sort_order) = 1
      AND ISNULL(process_row.analyst, 0) = 0;
END;
GO
