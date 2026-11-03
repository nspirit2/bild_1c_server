# Последовательный read-only target presence coordinator

## Исполнение только наблюдения

Координатор соединяет concrete RAC inventory adapter и concrete SQL metadata observer. На входе validated operation/fence/incarnation/reservation/canonical cluster/sql и два endpoint mapping pins. Имя генерируется строго bs_test_<reservation N>; два независимых observation GUID создаются внутри каждой invocation, не принимаются из API/job arguments и не переиспользуются из кэша.

Порядок RAC → SQL → paired assessment намеренно последовательный. Ошибка RAC прекращает шаг до SQL; ошибка SQL отбрасывает уже полученный RAC результат. Нет возврата partial-success/defaultAbsent. Каждое чтение имеет собственные fail-closed guards, совокупный deadline дополнительно проверяется монотонным Stopwatch и cancellation token между шагами и перед выдачей результата. Если зависимость игнорирует cancellation, hard preemptive wallclock termination не обещается; поздний success отвергается без отделённых Task.Run/WhenAny work.

Новые observation IDs защищают от случайного смешения labels между вызовами, но не аутентифицируют provider/tool, не доказывают freshness/continuous state и не блокируют намеренную relabel/replay. Server и RAC чтения не атомарны; BothObservedAbsent не резервирует имена и не подтверждает ownership. Физическая каноникализация aliases остаётся отдельной operator trust обязанностью. SQL complete visibility gate пока требует sysadmin: Worker права не менялись и coordinator к нему не подключён.

## Никакого повышения authority

В результате только существующий sealed TargetPresenceAssessmentResult: Scope, Observation, Executable=false, ScheduledJobsMustRemainBlocked=true. Нет restore lease/executable issuance, journal begin, stage settlement/quota release, Ready, CREATE/DROP/IB registration/restore/HTTP action. Это библиотечный building block, не автоматический запуск на стенде. Для нового внешнего mutation по-прежнему нужно конкретное разрешение владельца.

Scheduled-jobs-deny=on обязателен до/после restore и после restart; presence observation не заменяет readbacks этой политики. Security profile не mandatory gate.

Следующий содержательный срез после этого library coordinator — observation-only control plane с server assignment, durable local outbox и immutable server acknowledgement (design: [следующий интеграционный срез](observation-control-plane-next.md)); новые цепочки safety wrappers без доставки evidence не заменяют эту интеграцию.

## Проверки

Тесты concrete adapters используют fake IProcessRunner и DbConnection, проходя настоящий RAC parser, SQL visibility/identity gates и paired assessor. Проверяются4occupancy outcomes, exactfanout имен/пинов, разныеIDs внутри и между вызовами, прерывание цепочки при ошибках/отмене/timeout, отсутствие partial result и безопасные diagnostics. Это не реальный RAC+SQL E2E и не доказательство создания target. Filtered21/21passed; полный локальный Release **848passed/0failed**, SQL138explicitly skipped без opt-in. Locked restore и diff check successful. Независимый review не нашёл correctness blocker; общий elapsed timeout во время RAC покрыт, SQL async cleanup-specific overall timeout остаётся дополнительным coverage (SQL observer собственный cleanup-deadline test уже есть), не заявляем exhaustive cancellation coverage. Предыдущий assessment/deadlinefix commit31801dc CI Windows/Ubuntu и SQL138 зелёный. Ни EF migrations, ни package versions, ни production configuration этим срезом не меняются.
