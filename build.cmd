@echo off
cls

dotnet tool restore
dotnet fsi build.fsx build %*
