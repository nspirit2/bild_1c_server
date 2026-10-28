# Cleanup HTTP request boundaries

Добавлены2SQLfactsкреальномуAPIpipeline беззаменыauthentication/applicationports. ПроверяетсяневалидныйbodyсдействительнымAgentToken и отсутствиеimmutableResult/auditwrites.

Матрица: JSONnull/array, malformedGUID, missing/emptyIDs, fencing0/-1/fraction/overflow, unknownfield ->400; text/plainбезCSRF ->400InvalidCsrfToken. Изначальноожидавшийся415оказалсяневернойгипотезой: [ASP.NET AcceptsMatcherPolicy](https://raw.githubusercontent.com/dotnet/aspnetcore/v10.0.0/src/Http/Routing/src/Matching/AcceptsMatcherPolicy.cs) создаёт415rejectionEndpointсEmptyMetadata; middlewareAPIнеполучаетAgentMachineEndpoint exemption иfailclosedпроверяетCSRFдоэтого415handler. Этообъяснённоеповедение, неослаблялиCSRFдляисправлениятеста. Content-LengthсJSON+whitespaceвыше16KiB ->413 отapplicationmiddleware. ПроверкииспользуютреальнуюdisposableSQLDB, pinnedAuthorization иисториюauditдо/после.

Границы: этонеHTTPcanonicalJSONproof: duplicatesknownfields/quotednumbersSystem.Text.Jsonмогутпринимать, decodedtupleстрогопроверяетсяservice. TestServerнетестируетKestrelchunkedbodycap; Content-LengthcaseнеподменяетTLS/socket/Kestrelrequestlimitproof. ProductionJSONoptions/flags/routesнеизменены.

CIпредыдущегопакета9e98ebc: SQL40731668153success,UbuntuBuildsuccess,Windowsещёвработенамоментзаписи. НоваяdiagnosticsannotationизTRXпубликуеттолькоименаFailedtests, неexception/requestsecrets.

Локально **508.NETpassed,0failed,111SQLskipped**, **38Nodepassed**. **Windows111SQLpassed,0failed,0skipped**,45s, TestRuns\\CleanupBoundariesV2-20261028, portableVSTest18.0.1. Первыйпрогон110/111ошибсяожиданием415втесте; послеустановленнойпричинывыше fullsuiteповторён. ОстаточныхdisposableDB0/main4migrationsнеизменны.

9e98ebcтеперь[Build40731668164](https://github.com/nspirit2/bild_1c_server/actions/runs/40731668164) success наWindows/Ubuntu, [SQL40731668153](https://github.com/nspirit2/bild_1c_server/actions/runs/40731668153) success. СледовательноновыхFailedannotationsнет; причинаредкогоWindowsсбоя33b18d0неустановлена,необъявляемеёисправленной. Диагностикасохраненадлябудущегоповторения.
