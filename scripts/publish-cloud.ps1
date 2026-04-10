param(
    [string]$Configuration = "Release",
    [string]$OutputPath = "\\192.168.100.2\wwwroot\erp"
)

$projectPath = Join-Path $PSScriptRoot "..\src\Vendomat.Controller.Cloud\Vendomat.Controller.Cloud.csproj"
$resolvedProjectPath = (Resolve-Path $projectPath).Path

dotnet publish $resolvedProjectPath -c $Configuration -o $OutputPath
