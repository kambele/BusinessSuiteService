# Workflow service logs

The service writes to `C:\Debug\BusinessSuiteWorkflow-YYYY-MM-DD.txt`. When the file reaches its size limit, it starts a numbered file for the same day (for example, `BusinessSuiteWorkflow-2026-11-25.1.txt`). Existing logs are not deleted or truncated.

When deploying, keep the installed connection settings and merge these optional `appSettings`:

- `LogLevel`: defaults to `Info` for activity summaries, warnings and errors. Use `Debug` temporarily for detailed traces, `Warning` or `Error` for less output, or `Off` to disable logging. The legacy `Debug=1` setting no longer enables verbose logging.
- `LogMaxFileSizeMB`: defaults to `10` MB per file.

Normal logging omits idle checks and per-task traces. Repeat errors of the same operation, exception type and error code are limited to one line every five minutes; the next logged occurrence includes the number of repeats suppressed. Raw SQL traces and complete form payloads are no longer logged.
