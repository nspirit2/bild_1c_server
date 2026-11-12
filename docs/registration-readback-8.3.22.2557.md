# RAC 8.3.22.2557: observed registration output profile

## Evidence provenance and limits

12.11.2026 по запросу владельца выполнена read-only проверка ранее созданной закрытой smoke-ИБ через согласованный SSH с strict host-key verification. На Windows прочитан FileVersion/ProductVersion rac.exe: **8.3.22.2557**. Agent/RAS уже Running; start/stop/restart, CREATE, restore, SQL writes и изменение настроек не выполнялись.

`rac infobase info` с точным ранее записанным cluster/infobase UUID завершился exit0, stderr empty; stdout1219characters. Подтверждены spelling `dbms: MSSQLServer`, поля UUID/name/db-server/db-name и scheduled-jobs-deny:on, sessions-deny:on. Из stdout извлечён набор21field keys и завершающие пустые строки. Значения identity заменены согласованными примерами, остальные непустые extra values — `<redacted>` **на Windows до передачи через SSH**. Credentials расшифрованы только на Windows для этого вызова и не выводились.

Это наблюдение ОДНОГО состояния ОДНОЙ ИБ данной версии, не спецификация всех RAC versions и не byte-exact golden stdout. Sanitization нормализовала field spacing; тестовые строки далее — reconstructed fixtures observed shape, не нетронутый реальный вывод. CRLF/LF поддерживаются как явная bounded parser policy. Существующая smoke-ИБ имеет другое имя и историю: она не создана accepted durable-ACK protocol и НЕ принимается во владение через новый профиль. Readback не доказывает independently observed SQL incarnation или continuous block.

## Explicit profile selection

Новый profile ID `rac-registration-8.3.22.2557-v1` отдельный от `rac-registration-contract-v1`. Старый default parser остаётся strict six-field synthetic contract. Никакого auto-detection, fallback или выбора профиля из stdout нет. Result содержит фактически выбранный profile ID; profile-bound RequireMatch проверяет и scope, и profile. Legacy scope-only RequireMatch не доказывает profile binding.

Observer по умолчанию выключен. Для observed profile доверенная конфигурация требует exact `TrustedRacVersion=8.3.22.2557` и explicit OutputProfileId; это **operator assertion**, НЕ независимая проверка tool binary/version/hash на каждом запуске. Trusted tool resolver/inventory должны обеспечивать соответствие исполняемого файла этому assertion. Само наличие профиля не разрешает operational enablement, не регистрирует CLI/worker/DI и не выдаёт execution authority.

## Closed 21-key schema and secret discard

Шесть значимых полей: infobase, name, dbms, db-server, db-name, scheduled-jobs-deny. UUID, generated names, expected SQL literal и запрет заданий проверяются без normalization/alias inference по прежним строгим правилам.

15 дополнительных известных полей: db-user; security-level; license-distribution; sessions-deny; denied-from; denied-message; denied-parameter; denied-to; permission-code; external-session-manager-connection-string; external-session-manager-required; security-profile-name; safe-mode-security-profile-name; reserve-working-processes; descr.

Требуются все21keys ровно по одному. Unknown/duplicate/missing keys, malformed lines, interior blank lines, bareCR/control characters, превышение общего лимита16384UTF-16characters или лимитов отдельных полей отвергаются fixed errors. Trailing blank lines допускаются только в observed profile. Extra values могут быть пустыми; непустые bounded/controls-checked, не получают domain semantics и немедленно отбрасываются. В частности, наличие sessions-deny field не означает, что result проверяет/доказывает session block; result проверяет только scheduled-jobs-deny.

**Raw stdout/stderr потенциально секретны.** permission-code, external-session-manager-connection-string, descr и другие значения нельзя писать в generic logs/telemetry/exception messages. Parser/observer не возвращают или сериализуют extra values; trusted IProcessRunner должен передавать их только bounded callback, не логировать raw streams. Стандартный ProcessRunner не имеет собственного logger и отдаёт stdout/stderr callback. Managed string allocation при parsing неизбежна; secure memory erasure/forensic secrecy не обещаются. Result intentionally содержит лишь allowlisted identity/mapping metadata и inert flags.

## Non-authority and remaining gates

OwnershipEstablished, RestoreAllowed, FreshAuthority, RetryAllowed, Executable остаются false. Нет adoption, CREATE retry, job unblock, slot release, server-side ownership acceptance или restore grant. Independent SQL target/incarnation validation, physical endpoint attestation, coherent RAC/SQL observation lifecycle и durable evidence acceptance ещё нужны отдельно. Ни версия в options, ни успешный parser не заменяют эти проверки.

## Validation

Offline pure parser и fake-runner observer tests: оба profiles, exact version pin, missing/duplicate/unknown fields, extra secret canaries и JSON/diagnostics redaction, trailing/interior blanks, bounds/cancellation/deadlines и no fallback. Существующий saved-ACK bridge остаётся без изменений. Focused registration184passed (73additional cases); полный локальный Release2666passed/0failed,277SQLexplicitlyskipped; Node84passed/0failed. Независимый source/tests/docs review — без блокеров после фиксации профиля и negative scenarios. CI после push фиксируется отдельно.