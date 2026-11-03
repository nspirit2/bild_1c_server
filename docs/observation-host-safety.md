# Observation host safety boundary

## Default-off configuration

Agent:TargetObservation configuration остаётся operator-owned. Enabled=false не строит адаптеры, не читает RAC credential file и не связывает observation с Worker. При enabled режиме проверяются fixed AgentId, canonical cluster/SQL GUIDs, endpoint mapping GUIDs, один абсолютный RAC executable/workdir, фиксированные RAC/SQL endpoints и ограниченные timeouts. Никакие paths/endpoints/connection strings не поступают из CLI или assignment.

Pinned executor сверяет AgentId и все четыре configured canonical/mapping pins до inner executor и credential provider. Поэтому SQL pin mismatch не может вызвать RAC first-step IO. Coordinator может до этого выполнить server GET и journal assignment — гарантия относится к credential/process/SQL IO, не к нулевому HTTP/journal IO.

Рабочие каталоги, executable и конфигурация должны иметь operator-owned ACL. File.Exists/GetFullPath и allowlist не являются executable attestation, проверкой symlink/reparse traversal или automatic endpoint identity discovery.

## Protected RAC credentials

Отдельный observation-credentials.dpapi файл в Agent DataDirectory использует CurrentUser DPAPI. Это не существующий agent-token.dpapi и не LocalMachine credential envelope ручного стенда. Файл читает только служебная учётная запись, под которой он защищён. Payload имеет строгую JSON schema version1, clusterUser/clusterPassword; неизвестные/duplicate members, invalid UTF-8, controls и oversized data отклоняются. Исключения фиксированные, без payload/paths/inner diagnostics.

Import API создаёт защищённый файл без overwrite; оно не выполняется автоматически, не получает секреты из HTTP/CLI/config и не запускает observer. Binary plaintext buffers очищаются; immutable .NET strings невозможно обещать надёжно обнулить. DPAPI не заменяет directory ACL и не защищает от кода, работающего под той же учётной записью.

## Scope

Это source-only support. Live credentials не запрашивались и не читались, production/DataDirectory/службы не менялись. Actual SQL provider factory и Program routing подключаются отдельным срезом после тестов. Наблюдение не даёт execution lease/CREATE/restore; scheduled-jobs-deny=on остаётся mandatory до/после restore и после restart. Security profile не mandatory gate.

## Validation

Локальный полный Release: **1162passed/0failed**, Agent557cases (78добавлено), SQL164skipped без opt-in. Locked restore/diff check успешны; source review не нашёл actionable defect. Windows DPAPI runtime guard не означает Windows execution на Linux: реальный DPAPI round-trip должен подтверждаться Windows CI.

Тесты используют in-memory configuration, временные fake executable/directories, trusted fake inner executor и synthetic credentials. DPAPI round-trip выполняется только на Windows, pure parser cases работают на Linux; это не live RAC/SQL или реальная смена production credentials.
