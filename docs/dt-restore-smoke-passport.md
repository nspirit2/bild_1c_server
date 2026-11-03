# Паспорт первого DT restore-smoke

Статус на 03.11.2026: **ручной restore-smoke выполнен по отдельным разрешениям владельца; не разрешение на автоматический запуск**. Текущий planner review-only; автоматического исполнителя восстановления, target ownership journal и server restore grant ещё нет. Успех ручного smoke не расширяет stage-only authority.

## Минимальные сведения от владельца

| Поле | Сейчас |
| --- | --- |
| Реальный `.dt`: источник | Предоставлен владельцем как attachment; наличие обезличивания не установлено |
| Владелец/происхождение и разрешение использовать данные | Разрешение на тестовое использование получено |
| Размер и SHA256 | 16 687 989 bytes; DF76D7E118E018C93149B71CD18B79947C20B0C389640EA48D361041B30CB0D3, проверены после переноса |
| Версия и разрядность платформы 1С | 8.3.22.2557 x64; версия экспорта подтверждена владельцем |
| Хост платформы, Designer и лицензирование | sk-cicd-1c-01; Designer restore и DumpCfg exit0 03.11.2026; постоянная доступность/тип лицензии отдельно не установлены |
| Выделенный тестовый cluster/SQL target | Отдельная bs_smoke_ac40a41e29f04b64982add83b5461cc3, restore подтверждён логом Designer и SQL |

Пароли и raw bearer в паспорт, Git и чат не включать. Для будущего исполнения использовать protected credential references, отдельно подготовленные под service identity.

## Что проверит реализация перед запуском

1. Свежий server-owned grant с environment/job/lease/fence/target incarnation; stage-only grant не подходит.
2. Exact archive identity и hash, trusted local path/ACL, отсутствие reparse/race; ContentVerified history недостаточно для нового запуска.
3. Durable ownership journal для новой пустой тестовой IB/DB. Prefix `bs_test_`, mapping GUID и recipe hash не доказывают владение. Разные mapping GUID могут обозначать одну физическую цель; исполнитель обязан канонизировать external target identity и ставить lock на неё, а не только на GUID/hash плана.
4. Отсутствие конфликтующей операции и активных сессий; scheduled jobs запрещены **до** восстановления, запрет проверяется после восстановления и рестарта. Не снимать без явного разрешения владельца. По решению владельца security profile и отдельная egress isolation не являются обязательными gate развёртывания; запрет scheduled jobs сам по себе не блокирует HTTP/явные background tasks.
5. Trusted executable exact version и проверенный под неё CLI recipe без arbitrary flags/connection strings.
6. Ограниченный процесс, таймаут и журнал результата без credentials. Неизвестный исход — quarantine/reconciliation, а не blind retry.
7. Preparation profile и независимая verification перед publish/Ready. Exit code 0 сам по себе не достаточен.

## Разрушительная граница

DT restore полностью заменяет содержимое целевой IB. Первый smoke допускается только в отдельно созданную принадлежащую операции тестовую цель; существующую рабочую/непустую/неподтверждённую цель не перезаписывать. Сбой не разрешает автоматически drop database, снять reservation или повторить restore.

Rollback-план должен описывать частично изменённую цель, сохранение журнала/evidence и ручное решение администратора. Будущий cleanup stage artifact не равен удалению SQL/IB.

## Ограничения текущего прохода

Историческое состояние на 29.10.2026 (платформа ещё не была установлена) больше не описывает текущий стенд. К 03.11.2026 установлена x64 платформа, отдельно созданы/запущены Agent/RAS под virtual identities, выполнен restore DT и проверка Designer DumpCfg/DumpConfigToFiles. Приложение ENTERPRISE не запускалось; application health/Ready не заявлены. Службы 1С Manual, BuildSystem.Agent Stopped/Manual; общий firewall не менялся.

Checkpoint parser добавлен отдельно, не подключён к restore/worker. Автоматизация требует server-owned authority, durable canonical target ownership/lock, trust-protected credentials и reconciliation. Ручные smoke evidence не являются этими контрактами.
