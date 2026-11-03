# Серверное намерение restore и удержание артефакта

## Неисполняемый следующий срез

После StageCompleted/Completed stage lease не переиспользуется и не превращается в restore lease. Новый SQL-backed intent готовит отдельную операцию и удерживает staged artifact/целевые слоты; receipt всегда Executable=false. Это не grant, не доказательство пустой принадлежащей операции IB и не Ready.

Опция RestoreIntentOptions.Enabled по умолчанию false, независима от ArchiveStageOptions. В исходном hold-срезе HTTP route отсутствовал; следующим отдельным срезом добавляется human-only POST с cookie/CSRF (см. [API](restore-intent-api.md)). Machine claim/heartbeat, worker или команды restore отсутствуют. DI не включает исполнение.

## Источник пинов

Пользователь указывает environment и ожидаемые stage job/artifact IDs, но не пути, команды, credentials, target names или incarnation. Сервер читает точный завершённый stage tuple, immutable snapshot, archive identity, held reservation, текущие project/root/pool/agent bindings и действующий owner/admin доступ. Только DT. Новый intent/incarnation GUID генерирует сервер; incarnation пока **ожидаемая корреляция**, не фактическая история создания SQL/1С. Immutable record должен хранить exact source archive version/hash/size/format, preparation profile и snapshot fingerprint; повтор не должен молча принимать изменённый контент по прежнему ArtifactId.

Canonical cluster/SQL IDs приходят из operator-owned mapping pool→canonical IDs, не из запроса. Неизвестная mapping или расхождение с pool отклоняются. Это ограниченная конфигурационная гарантия: resolver не исследует DNS/SQL aliases и не доказывает, что два разных GUID не обозначают одну физическую машину. Production canonical registry/evidence предстоит отдельный срез.

## Транзакции и retained holds

Serializable SQL transaction, общий agent-control lock → root → pool → канонические слоты в детерминированном порядке. Независимая уникальность SQL-server+DB и cluster+IB исключает частичные коллизии; agent/pool/mapping alias не входят в ключ слота. Exact replay возвращает исходный immutable receipt при повторной текущей авторизации, не продлевает lease и не выдаёт исполнение. Пины/time удерживаются; UPDATE/DELETE intent запрещены через SaveChanges guards.

Restore intent несовместим с существующим retirement/cleanup/release. Обратные проверки запрещают retirement/cleanup/release удержанного artifact. Общий lock без симметричных проверок недостаточен: stage history остаётся завершённой и сама по себе не запрещает cleanup. Holds не снимаются по TTL/отзыву токена/ошибке; release/reconciliation API в этом срезе нет.

## Проверки перед фиксацией

Независимое read-only review проверило симметричные holds, порядок locks, отсутствие worker/grant route, сохранение stage history и coherent immutable archive/profile/snapshot pins. Отдельные SQL opt-in tests проверяют exact replay, конкурентные запросы и гонку retirement/restore; они используют только одноразовые metadata DB, не выполняют создание/restore внешней IB.

Локальный Release: **632 passed / 0 failed**, SQL125 явно skipped без opt-in; locked restore success; EF has-pending-model-changes — no changes. Новый Windows SQL2019 subset: **11 passed / 0 failed / 0 skipped**, включая migration и конкурентные сценарии. Portable test пакет отдельный от IIS/Agent deployments; ни API, ни Worker, ни 1С services для этих тестов не обновлялись. Полный Windows SQL2019 regression: **125 passed / 0 failed / 0 skipped**. TRX сохранены в отдельном TestRuns/restore-intent-20261103; fixture создаёт и удаляет только принадлежащие ему bs_ci_metadata_* базы. Итого проверено 757 .NET cases с реальным SQL. Первичный compile остановился на xUnit2031, исправлено Assert.Single(predicate); окончательные прогоны чистые.

Следующий regression пакет отдельно закрепляет DB-level независимые частичные SQL/IB slot collisions между разными agent/pool, CHECK запрет Executable=true и снятия scheduled-jobs policy, defensive copy dictionary и JSON невозможность выставить Executable=true на receipt. Это тесты metadata constraints, не доказательство физической каноничности внешнего сервера. После дополнения: локальный Release634passed/0failed (SQL128 skipped без opt-in), Windows SQL2019 **128passed/0failed/0skipped**, суммарно762 .NET cases. Прямые нарушения SQL/IB unique keys возвращают2601/2627 и соответствующий index; выключение запрета заданий или Executable=true возвращает547/check. Locked restore повторно успешен; временные RID lockfile изменения portable publish не включены в commit.

Commit73f6bb4: [Build41608854463](https://github.com/nspirit2/bild_1c_server/actions/runs/41608854463) success Windows/Ubuntu, [SQL41608854418](https://github.com/nspirit2/bild_1c_server/actions/runs/41608854418) success SQL2022/Linux. Таким образом миграция и SQL subset проверены как на SQL2019/Windows, так и на CI SQL2022/Linux; это не production deployment.

Commitfb82fdc: [Build41609787846](https://github.com/nspirit2/bild_1c_server/actions/runs/41609787846) success Windows/Ubuntu, [SQL41609787875](https://github.com/nspirit2/bild_1c_server/actions/runs/41609787875) success, включая 128 SQL tests без skips.

## Что не меняется

Stage job/lease, environment/request states, reservation/quota и HasUnsettledProvisioningOperation сохраняются. SQL EF migration только в исходниках/CI; рабочая metadata DB не мигрируется автоматически. Новые внешние IB/SQL DB не создаются и текущая ручная smoke IB не усыновляется.

Для будущего destructive executor нужны отдельные server restore issuance/lease, durable canonical ownership/creation history, физическая блокировка цели и reconciliation. Проверки scheduled-jobs-deny=on обязательны до/после restore и после restart; security profile не обязательный gate. Intent/readback/local journal не заменяют эти условия.
