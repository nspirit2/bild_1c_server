# Observation host safety boundary

## Default-off configuration

Agent:TargetObservation configuration остаётся operator-owned. Enabled=false не строит адаптеры, не читает RAC credential file и не связывает observation с Worker. При enabled режиме проверяются fixed AgentId, canonical cluster/SQL GUIDs, endpoint mapping GUIDs, один абсолютный RAC executable/workdir, фиксированные RAC/SQL endpoints и ограниченные timeouts. Никакие paths/endpoints/connection strings не поступают из CLI или assignment.

Pinned executor сверяет AgentId и все четыре configured canonical/mapping pins до inner executor и credential provider. Поэтому SQL pin mismatch не может вызвать RAC first-step IO. Coordinator может до этого выполнить server GET и journal assignment — гарантия относится к credential/process/SQL IO, не к нулевому HTTP/journal IO.

Рабочие каталоги, executable и конфигурация должны иметь operator-owned ACL. File.Exists/GetFullPath и allowlist не являются executable attestation, проверкой symlink/reparse traversal или automatic endpoint identity discovery.

## Protected RAC credentials

Отдельный observation-credentials.dpapi файл в Agent DataDirectory использует CurrentUser DPAPI. Это не существующий agent-token.dpapi и не LocalMachine credential envelope ручного стенда. Файл читает только служебная учётная запись, под которой он защищён. Payload имеет строгую JSON schema version1, clusterUser/clusterPassword; неизвестные/duplicate members, invalid UTF-8, controls и oversized data отклоняются. Исключения фиксированные, без payload/paths/inner diagnostics.

Import API создаёт защищённый файл без overwrite; оно не выполняется автоматически, не получает секреты из HTTP/CLI/config и не запускает observer. Binary plaintext buffers очищаются; immutable .NET strings невозможно обещать надёжно обнулить. DPAPI не заменяет directory ACL и не защищает от кода, работающего под той же учётной записью.

## Scope

Это source-only support. Live credentials не запрашивались и не читались, production/DataDirectory/службы не менялись. Concrete SQL provider factory и Program routing подключены: точная команда `BuildSystem.Agent.exe observe-target <ObservationRequestId-D>` завершается до Windows service/Worker/ArchiveStaging registration. Конфигурация берётся из operator-owned файлов/environment, positional args не являются configuration overrides. Enabled=false возвращает4/TargetObservationDisabled. Invalid args64, cancellation130, config/storage failure5, coordination blocked4;0означает historical Recorded, не Ready. Наблюдение не даёт execution lease/CREATE/restore; scheduled-jobs-deny=on остаётся mandatory до/после restore и после restart. Security profile не mandatory gate.

## Concrete factory и concurrency

Один trusted RAC executable/internal tool ID/allowed workdir, fixed read-only inventory; SQL connection создаётся через SqlClient6.1.7, structured builder с IntegratedSecurity=true, InitialCatalog=master, Encrypt=Mandatory, TrustServerCertificate=false, bounded timeout, Pooling=false, fixed ApplicationName. Обнаруженный уязвимый transitive PKCS9.0.11 заменён прямым10.0.13 pin; audit warnings не подавлены, locked restore проходит.

Explicit invocation удерживает observation-command.lock через FileShare.None до завершения journal/transport работы. Это сериализует только explicit observation commands для одного DataDirectory. **Оператор должен остановить service/другие процессы, работающие с этим journal**: они не участвуют в lock protocol, lock не является межагентным target lease и не решает physical exclusion. Crash до enqueue может повторить read-only observation; после enqueue отправляется та же immutable история.

## Timer-sensitive test hardening

Предыдущий c0a8859 Windows CI упал в RealTimeoutRejectsEvenCompleteValidInventory и GuardCallbacksNeverOverlapAndStopBeforeSuccessfulReturn (только annotations, failed TRX недоступен без auth). Это не подтверждает дефект production или конкретную причину. Renewal test переведён на существующий ControlledClock и barriers: periodic guard удерживается во время фактического завершения copy, final guard отдельно освобождается; сохраняются no-overlap/Enqueued/pending-artifact/no-post-return-calls assertions, без20ms/60mssleep. Production renewal/lease semantics не изменены. RAC timeout integration пока не ослаблен:2sbudget vs startup возможен, но full inventory assertion сохранён.

Regression full Release **1185passed/0failed**, Agent580cases; SQL164skipped без opt-in; executable3/3 +deterministicrenewal1/1passed. Последующий Windows CI обязателен.

## Validation

Wiring full Release: **1182passed/0failed**, Agent577cases; SQL164skipped без opt-in. Executable smoke без targets: disabled4, extra flag64 и malformed appsettings5 дают только fixed statuses, без framework exception/config content. Startup host creation observation-mode ошибки тоже redacted. Source review исправлен по этому startup boundary; Windows cross-process lock contention остаётся отдельным future stress case. Добавлены три executable regression cases (invalid flag до config load, malformed config redaction, disabled до worker setup), запускающие Agent entrypoint с test runtimeconfig/deps для pruned AspNetCore graph.

Локальный полный Release: **1162passed/0failed**, Agent557cases (78добавлено), SQL164skipped без opt-in. Locked restore/diff check успешны; source review не нашёл actionable defect. Windows DPAPI runtime guard не означает Windows execution на Linux: реальный DPAPI round-trip должен подтверждаться Windows CI.

Тесты используют in-memory configuration, временные fake executable/directories, trusted fake inner executor и synthetic credentials. DPAPI round-trip выполняется только на Windows, pure parser cases работают на Linux; это не live RAC/SQL или реальная смена production credentials.
