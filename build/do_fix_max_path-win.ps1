<#
.SYNOPSIS
    Enables long path support (paths longer than 260 chars) in Windows.
.DESCRIPTION
    Sets the registry key "LongPathsEnabled" to 1 under HKLM\SYSTEM\CurrentControlSet\Control\FileSystem.
.NOTES
    Requires Administrator privileges. A system reboot is recommended.
#>

# Установите значение LongPathsEnabled в 1
$regPath = "HKLM:\SYSTEM\CurrentControlSet\Control\FileSystem"
$regName = "LongPathsEnabled"
$regValue = 1

# Проверка, существует ли ключ реестра
if (-not (Test-Path $regPath)) {
    New-Item -Path $regPath -Force
}

# Установка значения
Set-ItemProperty -Path $regPath -Name $regName -Value $regValue

# Вывод сообщения о завершении
Write-Host "Поддержка длинных путей включена. Пожалуйста, перезагрузите компьютер для применения изменений."