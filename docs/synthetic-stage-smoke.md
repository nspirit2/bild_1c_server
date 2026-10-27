# Synthetic stage smoke: ограничения и след выполнения

Scope согласован владельцем: dedicated BuildSystem host, синтетический sample без production data, stage-only, никаких restore/SQL1C подключений. Имя проекта/файла явно сообщает SYNTHETIC NOT REAL 1C. Метаданные Ready версии означают лишь opt-in публикацию для этого теста, не утверждение валидности dt. EstimatedDbBytes синтетическая оценка, не замер БД.

## Подготовка

Созданы E:\BuildSystem\SyntheticStage\{Agent,Data,Source,Staging,Ops}. Parent ACL SYSTEM/Admin full, LOCAL SERVICE traverse/read. Data/Staging Modify LOCAL SERVICE; Agent/Source RX. Ни SQL credential, ни admin password агенту не передаются. Published portable agent с Windows SQLite native asset, SDK на хосте не ставился.

Agent service планируется под NT AUTHORITY\LOCAL SERVICE; DPAPI CurrentUser import должен выполняться под той же identity, не под SSH user. Temporary credential handoff — только в protected Data directory, удаляется после успешного import; import script не выводит token/exception details. Если import неудачен, handoff остаётся protected для расследования, no automatic rotation/reset.

Отдельный ops seed tool нужен потому, что текущие human admin API не умеют создавать projects/roots/pools/quotas. Не подменять это browser provisioning: явный scoped EF seed/audit по согласию владельца, а не raw SQL произвольная правка. Immutable archive snapshot не изменять. Stage worker не рекламирует capability, работает отдельным opt-in loop, никогда рядом с ProbeWorker на том же journal.

## Результат реального прогона

27.10.2026: job `0b85ecbb-3f91-40c6-a0c0-72785187093c` **StageCompleted**, lease **Completed**, fence1; StagedArtifact **ContentVerified**,65536 bytes. Реальный staged файл также65536 bytes и SHA256 `3DE39E2EC6A0876E0F6C7E6D392E58C61A667FC59A59C55C1992809DCCEC7EA8`, совпадает с source/version/receipt. Project `d845f6ae-c5b3-479a-9de6-7ffea42aecc8`, request `2cb5432d-b477-499f-8b52-7b480a4b0f08`, environment `adbd2f8c-f05e-4347-a244-31e81dd8d55c`.

Environment остаётся Requested, HasUnsettledProvisioningOperation=true, reservationHeld, requestPending — ожидаемые значения stage-only, не Ready/успешный restore. Стабильный order key хранится только protected manifest и не выводится helper. Реальный browser login владельцем подтверждён ранее; этот заказ создан ops application port, **не browser E2E POST**. Enrollment/approval/capabilities/inventory также application ports, worker poll/artifact/completion — реальные HTTPS API под агентским credential.

### Найденные operational проблемы

- Native import пишет prompt в stderr; PowerShell5.1 ErrorActionStop перехватывал это как failure до импорта. Исправлено использованием ProcessStartInfo redirected stdin/stdout/stderr, без вывода token. Повтор import под LocalService task exit0, handoff отсутствует; task удалён.
- Hostname на самом сервере резолвится в `::1` и10.21.0.149. ЧерезIPv6 исходный Schannel/.NET client получал reset, через forcedIPv4 строгий TLSHTTP401. Попытка IPv6 IIS binding вызвала остановку сайта на время диагностики; восстановлен Start-Website, probe binding удалён exact ServerManagerAPI, финальный HTTPS200. HTTPsys final sslcert list содержит только исходныйIPv4 certificate. Дополнительный netshdelete получил Error2, проверено что запись уже отсутствует, а не проигнорирована ошибка.
- Решение без TLS bypass: только agent service Environment `DOTNET_SYSTEM_NET_DISABLEIPV6=1`, restart. После этого worker штатно получил lease/скопировал/отправил receipt/completion. Ни глобальныйIPv6, ни DNS/hosts, ни certificate validation не отключались.

### Финальное состояние

Agent Windows service `BuildSystem.Agent`, LocalService, Manual, **Stopped** после успешного теста. Server ArchiveStage.Enabled сноваfalse (восстановлен protected previous config), poolrecycled; final HTTPS/stage200. Agent per-processIPv4 override оставлен для следующего согласованного прогона. Source/staging/journal/DPAPI/DBrows сохранены для reconciliation, cleanup/reset не делались. Ownerquota1 намеренно занята Held reservation; второй заказ не создавать до предусмотренного lifecycle или отдельного нового testowner. Никаких реальных ИБ/SQLtarget не существует: pool cluster/server IDs synthetic, verified flags относятся только к noexecution fixture, не production assurance.

Следующий шаг: UI исторический просмотр этого job владельцем, затем durable management/lifecycle перед новым заказом. Автоматическое снятие quota/reset запрещено. Настоящий dt/restore требует отдельной разрешённой среды и правдивой проверки targets. Local ops utility buildpassed0warnings; product code не менялся.
