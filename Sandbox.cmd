@echo off
rem Запуск игры (Debug-сборка; пересобрать: dotnet build Game.slnx). Путь считается от папки этого файла,
rem поэтому запуск работает, где бы ни лежал проект.
start "" "%~dp0src\Client\bin\Debug\net10.0\Sandbox.exe" %*
