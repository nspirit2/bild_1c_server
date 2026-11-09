# RAC CREATE acknowledgement boundary

## Purpose

Чистый bounded classifier результата уже потенциально выполненного процесса. Он не запускает RAC, не пишет journal, не выдаёт execution grant и не связывает выход процесса с реальной SQL DB или регистрацией 1С. Scope — correlation pins, не доказательство mapping/authority/freshness.

`AcknowledgementObserved` означает только: exit 0, положительный process ID, отсутствие cancellation/capture failure/truncation, пустой stderr и строго распознанный единственный `infobase : <GUID-D>`. UUID ненулевой, accepted uppercase/lowercase hex. Допускаются ASCII space/tab вокруг ключа/colon/value, внешние blank lines и LF/CRLF. Неизвестные поля, несколько строк данных, duplicate UUID (даже одинаковый), raw UUID, bare CR, control characters, redaction marker, null/oversize output отвергаются. Максимум stdout 4096 символов. Формат консервативный: неподдерживаемый вывод версии RAC остаётся reconciliation, не попытка угадать UUID.

Все остальные исходы — `ReconciliationRequired / OutcomeUnknown`, включая nonzero exit, timeout, cancellation, null result и ошибку capture. Даже nonzero exit не доказывает отсутствия side effects. Classifier не отвечает, запускался ли процесс; вызывается только для потенциально начатой попытки. Pre-launch rejection обрабатывается отдельно, не разрешая повтор после durable start.

Во всех случаях: `Executable=false`, `RetryAllowed=false`, `RequiresIndependentVerification=true`, `ScheduledJobsMustRemainBlocked=true`. Нет Created/Ready/ownership/success grant, DROP/adoption/reset/automatic retry. Корректный UUID — кандидат для независимого exact-registration readback, а не подтверждённая target identity.

## Durable integration remains separate

Существующий local journal записывает единственную CreationInFlight attempt до mutation; replay `NewlyRecorded=false` не повторяет CREATE. CreationInFlight не переводится этим classifier в success или ownership. Unknown outcome требует durable quarantine (`OutcomeUnknown`) с сохранением pins/slots; ACK всё ещё требует fresh SQL identity/presence, exact RAC registration, deny readback и online authority перед последующей mutation. Crash между invocation/ACK/persist остаётся reconciliation, не поводом повторять CREATE.

Ни один worker/HTTP/CLI consumer не подключён; нет доступа к стенду, live CREATE/restore/drop и новой схемы серверной БД. `ProcessResult`, scope и строки подаются непосредственно в pure method; raw output/credentials не сохраняются в результате или диагностике.

## Verification

39 expanded classifier cases прошли. Negative non-ASCII whitespace test выявил, что Guid.TryParseExact принимает внешние Unicode whitespace: добавлены exact length 36 и canonical D roundtrip без этого tolerance; после fix весь targeted run прошёл. Независимый read-only review — без blockers по явно разрешённым ASCII space/TAB и blank LF/CRLF framing. Полный Release/CI проверяются отдельно. Это parser-contract testing, не real RAC/version compatibility или физическое создание SQL DB. Recipe e642473: Windows/Linux CI42580801352 и SQL CI42580801233 success.