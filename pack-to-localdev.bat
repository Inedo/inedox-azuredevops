@echo off

dotnet new tool-manifest --force
dotnet tool install inedo.extensionpackager

cd AzureDevOps\InedoExtension
dotnet inedoxpack pack . C:\LocalDev\BuildMaster\Extensions\AzureDevOps.upack --build=Debug -o
cd ..\..