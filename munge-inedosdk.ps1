$pkgName = "Inedo.SDK"
$pkgProjFile= "c:\Projects\Inedo.sdk\Inedo.SDK\Inedo.SDK.csproj"

$slnFile = "C:\Projects\inedox-AzureDevOps\AzureDevOps\AzureDevOps.sln"
$projFilesToMunge = @( `
  "C:\Projects\inedox-AzureDevOps\AzureDevOps\InedoExtension\InedoExtension.csproj"
)

dotnet sln "$slnFile" add "$pkgProjFile"
foreach ($projFile in $projFilesToMunge) {
  dotnet remove "$projFile" package "$pkgName"
  dotnet add $projFile reference $pkgProjFile
}

pause