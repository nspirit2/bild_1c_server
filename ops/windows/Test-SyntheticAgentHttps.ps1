$ErrorActionPreference='Stop'
Add-Type -AssemblyName System.Net.Http
$c=New-Object Net.Http.HttpClient
try {
 $r=$c.GetAsync('https://sk-cicd-1c-01.erkapharm.ru/api/projects/browse').GetAwaiter().GetResult()
 [IO.File]::WriteAllText('E:\BuildSystem\SyntheticStage\Data\https-check.txt', 'HTTPS status: '+[int]$r.StatusCode)
} catch {
 $e=$_.Exception; $types=@(); while($null -ne $e){$types+=($e.GetType().FullName+': '+$e.Message);$e=$e.InnerException}
 [IO.File]::WriteAllText('E:\BuildSystem\SyntheticStage\Data\https-check.txt',($types -join [Environment]::NewLine))
} finally {$c.Dispose()}
# end
