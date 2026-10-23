# Каталог архивов и content staging: первый срез

## Интеграция предыдущих пакетов

Все готовые пакеты объединены в основной ветке `main` merge-коммитом `472e6d9` и отправлены в origin по поручению пользователя. Включены domain, order API, agent control-plane, ProcessRunner/Pipeline, подтверждённые SQL и Windows/Ubuntu CI отчёты и решение Windows-only. Следующая работа ведётся отдельно в `feat/archive-staging`.

## Граница нового пакета

Цель — не восстанавливать ИБ, а фиксировать идентичность архивного файла и проверенные байты staging. Сервер принимает частичные bounded отчёты от доверенного агента, а отдельная библиотека агента умеет fingerprint/stage явно выбранного файла в настроенном root. Нет автоматического обхода папок, расписания indexing, браузерной загрузки, UI каталога и реальных 1С/SQL providers. Локальный artifact handle — детерминированный UUIDv8 из SHA256 namespace пары reservation/version (строка GUID в формате N). Он ещё не зарегистрирован как серверный ArtifactId из pipeline: синтаксически подходящий GUID не заменяет server receipt и проверку reservation/lease.

Worker не получает дополнительных capabilities: по-прежнему только ControlPlaneProbe. Standalone staging не зарегистрирован как archive.stage@1 и не вызывается заданиями ProvisionArchive. Для подключения потребуются server plan store, artifact receipt/resource journal, reservation/lease guard и интеграционные испытания. Типизированный library request не означает проверку серверной reservation authorization.

## Серверный inventory

`POST /api/agent/archives/inventory` использует AgentToken/HTTPS; cookie пользователя недостаточно, CSRF не требуется только этому machine endpoint. Сервис повторно проверяет действующий токен, Approved-state и root binding внутри той же serializable transaction под общей блокировкой с rotate/revoke. Root.AgentScopeId в этом срезе — точный AgentId; также проверяется доступность enrolled pool.

JSON: RootId и Entries (не более100), каждый элемент RelativePath, Sha256, SizeBytes, Format (dt/bak). Абсолютный путь root сервер не принимает. Сохраняется canonical relative path с '/' и нормализованный SHA256. Идентичность пути явно case-insensitive, accent/kana/width-sensitive: SQL collation `Latin1_General_100_CI_AS_KS_WS` закреплена новой миграцией ArchivePathIdentity, а не зависит от default DB. Перед применением к существующей базе нужно проверить case-only дубликаты и конфликтующие имена: миграция не удаляет их автоматически. HTTP body cap остаётся16KiB, поэтому крупный каталог отправляется малыми пачками, не одним запросом. Это частичный report, не подтверждение полного сканирования: отсутствие файла в пачке не удаляет другие записи.

Новая версия имеет State=Discovered, EstimatedDbBytes=0. Hash+extension не подтверждают валидность .dt/.bak, совместимость и необходимый размер БД; такие версии НЕ доступны для заказа как Ready. Текущий пакет не содержит endpoint ручного обхода этой проверки.

При замене содержимого по прежнему относительному пути прежние версии становятся Unavailable. Snapshot уже созданного заказа не меняется; stage всё равно обязан сравнить pinned hash/size. Идентичный отчёт возвращает прежнюю версию, не создаёт дубликаты и не переводит её принудительно в Ready. Конфликт метаданных для того же hash отклоняет всю пачку. Размеры и пути других root не раскрываются в error payload.

## Локальный fingerprint и stage

RootId разрешается только через operator-owned mappings. Не допускаются выход из root, ADS/traversal, reparse/symlink ancestors и посторонние расширения. Поставщик обязан публиковать завершённый файл атомарным rename; это явная конфигурационная предпосылка, не эвристика «файл старый — значит готов».

Источник читается один раз для fingerprint и ещё раз для stage. Stage копирует в собственный temporary location, вычисляет SHA256/size, сравнивает с выбранной версией, flush и только затем атомарно публикует готовую пару data/manifest. Source никогда не удаляется и не перезаписывается. Retry проверяет существующий artifact повторно, а не доверяет одному имени/manifest. Частичные временные файлы не считаются готовыми.

Это проверка CONTENT identity, не format validation. .bak требует metadata/filelist/VERIFYONLY и проверки прав SQL service account в последующем provider. .dt требует проверки реальными средствами платформы и фактического restore в новую базу. Локальный staging path агента нельзя автоматически передавать удалённому SQL как его путь.

## Безопасность и эксплуатация

Windows ACL staging/root и служебные identities обязательны; библиотечная проверка путей не является sandbox против локального администратора. Reparse prechecks не устраняют TOCTOU при злонамеренной замене каталогов; нужен защищённый ACL. Хеш не шифрует данные: архивы содержат чувствительные данные, staging требует защиты и политики retention.

Retention/GC и очистка неизвестных temp после crash не выполняются вслепую. Нельзя удалять artifact, используемый restore. Для реальной эксплуатации предстоят durability/atomic rename tests на Windows/UNC и дисках целевого стенда, отдельные квоты reservation и контролируемая доставка для SQL-хоста.

## Проверки

Фактический локальный результат: locked restore успешен; Release build 0 warnings/0 errors; **157 passed, 0 failed, 20 SQL skipped**. Включает domain39, API36, Worker11, Process22, Pipeline26, Archive23. Из новых SQL-сценариев4 только скомпилированы и явно skipped без SQL opt-in. Новая миграция сгенерирована EF, SQL script проверен на drop/recreate unique index при ALTER COLLATE; has-pending-model-changes изменений не обнаружил. Реального применения третьей миграции к SQL здесь ещё не было.

Read-only ревью выявило зависимость путей от DB collation (исправлено явной collation), A→B→A invalidation (исправлено), потенциально бесконечное ожидание IO lock (ограничено5s), quota/copy bounds и overlap root drive (исправлены). Редкие несовпадения SQL linguistic equality и OrdinalIgnoreCase в новых batch могут дать unique conflict; существующий несовпадающий path отклоняется fail-closed, не объединяется молча. Миграцию следует сначала применить на disposable SQL CI.

Локальная квота включает data/manifest и неизвестные временные/root файлы. Per-operation lock не является распределённым lease для UNC. Flush(true)+Directory.Move не обещают сохранность directory metadata при полном power loss. Symlink-тест на Windows может не выполняться при отсутствии привилегии; полноценная проверка Windows/UNC нового пакета ещё не проведена.

Пакет закоммичен и отправлен в feature-ветку коммитом `efa1dec`, в main пока не слит. Запущены [SQL CI](https://github.com/nspirit2/bild_1c_server/actions/runs/39920608723) и [Windows/Linux CI](https://github.com/nspirit2/bild_1c_server/actions/runs/39920608757). SQL workflow теперь дополнительно разбирает TRX и отклоняет прогон при пропусках или количестве результатов меньше20; один зелёный test command больше не скрывает skipped SQL suite. Оба прогона завершились success для `efa1dec`: Windows и Ubuntu jobs зелёные; SQL job включая отдельный TRX guard зелёный. Следовательно SQL suite содержит не менее20 результатов и все они Passed (на этом коммите в suite20 тестов). Точные remote числа Windows/Ubuntu не утверждаются без чтения их TRX; локально подтверждены157 passed. Третья миграция применена в disposable SQL CI через migration integration test; это не приёмка существующей корпоративной БД с историческими конфликтами путей. Windows filesystem tests прошли как job, но привилегированный symlink/UNC/SCM/DPAPI всё ещё не считаются проверенными. NuGet audit всех17 проектов не выявил известных уязвимостей. Предыдущие CI результаты не выдаются за проверку нового кода.
