# Совместная read-only оценка наличия целевой IB/SQL DB

## Почему отдельный контракт

Один RAC inventory и один SQL sys.databases lookup отвечают на разные вопросы. Они могут быть собраны в разные моменты и даже при неверно сконфигурированных endpoint mappings. Следующий чистый assessment должен отвергать несовпадение operation/fence/target incarnation/reservation, canonical cluster/sql IDs, endpoint mapping IDs, имён и двух observation IDs; эти значения лишь корреляционные labels, не криптографическая аутентификация и не физическая каноникализация aliases.

Результат — одна из четырёх комбинаций двух наблюдений (оба Absent, только IB Present, только SQL DB Present, оба Present). Partial occupancy обязательно видим, а не превращён в «цель свободна». Presence не доказывает, кто создал IB/DB. Absent в обоих источниках не является владением свободным именем: между запросами и будущим CREATE нет общего транзакционного lock, возможны сторонние операции, кэш, новые aliases и replay. Оба observer требуют отдельных гарантий: RAC normal read-only authenticated command и SQL master/sysadmin full metadata. Ни один из них не сообщает server restore grant или durable creation history.

Контракт не выполняет IO, не прикладывает command authority и не меняет состояния. Executable навсегда false; ScheduledJobsMustRemainBlocked навсегда true. Нет CanCreate/CanRestore, Ready, adoption, cleanup/release, Worker registration и автоматической реакции на BothObservedAbsent. Server nonexecutable intent остаётся неизменным; stage-only lease не повышается.

## Проверки

Нужны точные совпадения всех scope pins и все4 occupancy outcomes, mismatch-fail-closed, отличие IDs, недоступные/null результаты, immutability и отсутствие authority в публичном DTO. Повтор ранее сохранённых результатов сам по себе успешно сопоставляет labels — специальный negative test демонстрирует отсутствие replay/freshness protection, чтобы будущий coordinator не полагался на assessor как на grant.

Независимый read-only review: замечаний к exact pins/null/all4 outcomes/no-authority не обнаружено. Filtered14passed, полный Release после SQL deadline fix **827passed/0failed**, SQL138explicitly skipped без opt-in. Последний WindowsSQL2019 прогон138/138passed предыдущегоSQLobserver; paired assessment DB IO не меняет. Locked restore и diff check successful. Новый result имеет ровно4 getter-only поля, internal ctor; SQL result reflection используется только в tests и не расширяет production surface.

Commit31801dc: [SQL41618690577](https://github.com/nspirit2/bild_1c_server/actions/runs/41618690577) success SQL2022/Linux138/138; [Build41618690453](https://github.com/nspirit2/bild_1c_server/actions/runs/41618690453) Windows/Ubuntu success, включая исправленный SQL timeout regression.

Для настоящего CREATE/restore позже потребуются отдельный executable server issuance + fence/lease, trusted physical-target ownership/barrier и внешняя reconciliation; owner approval на новое side effect не получено. При restore всегда scheduled-jobs-deny=on до, после и после restart. Security profile не mandatory gate.
