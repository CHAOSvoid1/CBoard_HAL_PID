param([switch]$SelfTest)

Add-Type -AssemblyName System.Windows.Forms
Add-Type -AssemblyName System.Drawing

[System.Windows.Forms.Application]::EnableVisualStyles()

$script:ProjectRoot = Split-Path -Parent $PSScriptRoot
$script:ConfigPath = Join-Path $script:ProjectRoot 'User_Files\app_config.h'
$script:KeilProject = Join-Path $script:ProjectRoot 'MDK-ARM\CBoard.uvprojx'
$script:Serial = $null
$script:RxBinary = New-Object 'System.Collections.Generic.List[byte]'
$script:IsClosing = $false

function New-Label([string]$text, [int]$x, [int]$y, [int]$w = 120) {
    $c = New-Object System.Windows.Forms.Label
    $c.Text = $text
    $c.Location = New-Object System.Drawing.Point($x, $y)
    $c.Size = New-Object System.Drawing.Size($w, 24)
    $c.TextAlign = [System.Drawing.ContentAlignment]::MiddleLeft
    return $c
}

function New-Button([string]$text, [int]$x, [int]$y, [int]$w = 110) {
    $c = New-Object System.Windows.Forms.Button
    $c.Text = $text
    $c.Location = New-Object System.Drawing.Point($x, $y)
    $c.Size = New-Object System.Drawing.Size($w, 30)
    return $c
}

function New-Combo([int]$x, [int]$y, [int]$w = 180) {
    $c = New-Object System.Windows.Forms.ComboBox
    $c.Location = New-Object System.Drawing.Point($x, $y)
    $c.Size = New-Object System.Drawing.Size($w, 25)
    $c.DropDownStyle = [System.Windows.Forms.ComboBoxStyle]::DropDownList
    return $c
}

function Set-ConfigDefine([string]$text, [string]$name, [string]$value) {
    $pattern = '(?m)^\s*#define\s+' + [regex]::Escape($name) + '\s+.*$'
    $replacement = '#define ' + $name.PadRight(31) + $value
    if ([regex]::IsMatch($text, $pattern)) {
        return [regex]::Replace($text, $pattern, $replacement)
    }
    throw "配置项不存在: $name"
}

function Get-ConfigValue([string]$text, [string]$name) {
    $match = [regex]::Match($text, '(?m)^\s*#define\s+' + [regex]::Escape($name) + '\s+([^\s/]+)')
    if ($match.Success) { return $match.Groups[1].Value }
    return ''
}

function Write-Utf8NoBom([string]$path, [string]$text) {
    $encoding = New-Object System.Text.UTF8Encoding($false)
    [System.IO.File]::WriteAllText($path, $text, $encoding)
}

function Add-Log([string]$text) {
    if ($script:IsClosing) { return }
    $stamp = Get-Date -Format 'HH:mm:ss'
    $txtLog.AppendText("[$stamp] $text`r`n")
    $txtLog.SelectionStart = $txtLog.TextLength
    $txtLog.ScrollToCaret()
}

function Refresh-ComPorts {
    $selected = $cmbCom.Text
    $cmbCom.Items.Clear()
    [System.IO.Ports.SerialPort]::GetPortNames() | Sort-Object | ForEach-Object { [void]$cmbCom.Items.Add($_) }
    if ($cmbCom.Items.Contains($selected)) {
        $cmbCom.SelectedItem = $selected
    } elseif ($cmbCom.Items.Count -gt 0) {
        $cmbCom.SelectedIndex = 0
    }
}

function Send-Command([string]$command) {
    if ($null -eq $script:Serial -or -not $script:Serial.IsOpen) {
        [System.Windows.Forms.MessageBox]::Show('请先连接串口。', '串口未连接') | Out-Null
        return
    }
    try {
        $script:Serial.Write($command + "`r`n")
        Add-Log "TX> $command"
    } catch {
        Add-Log "发送失败: $($_.Exception.Message)"
    }
}

function Decode-JustFloat {
    while ($script:RxBinary.Count -ge 36) {
        $tailIndex = -1
        for ($i = 32; $i -le $script:RxBinary.Count - 4; $i++) {
            if ($script:RxBinary[$i] -eq 0x00 -and $script:RxBinary[$i + 1] -eq 0x00 -and
                $script:RxBinary[$i + 2] -eq 0x80 -and $script:RxBinary[$i + 3] -eq 0x7F) {
                $tailIndex = $i
                break
            }
        }
        if ($tailIndex -lt 32) {
            if ($script:RxBinary.Count -gt 256) { $script:RxBinary.RemoveRange(0, $script:RxBinary.Count - 64) }
            return
        }
        $start = $tailIndex - 32
        $values = @()
        for ($channel = 0; $channel -lt 8; $channel++) {
            $bytes = New-Object byte[] 4
            for ($b = 0; $b -lt 4; $b++) { $bytes[$b] = $script:RxBinary[$start + $channel * 4 + $b] }
            $values += [BitConverter]::ToSingle($bytes, 0)
        }
        $script:RxBinary.RemoveRange(0, $tailIndex + 4)
        Add-Log ('JF> speed {0:F1}/{1:F1} rpm, pos {2:F2}/{3:F2} deg, current {4:F0}, temp {5:F0}, online {6:F0}, pid {7:F1}' -f $values)
    }
}

$form = New-Object System.Windows.Forms.Form
$form.Text = 'C板单电机测试台 - M2006+C610 / M3508+C620'
$form.Size = New-Object System.Drawing.Size(1060, 760)
$form.StartPosition = [System.Windows.Forms.FormStartPosition]::CenterScreen
$form.MinimumSize = New-Object System.Drawing.Size(1060, 760)
$form.Font = New-Object System.Drawing.Font('Microsoft YaHei UI', 9)

$grpConfig = New-Object System.Windows.Forms.GroupBox
$grpConfig.Text = '1. 单电机固件配置（一次只测试一台，保存后需重新编译烧录）'
$grpConfig.Location = New-Object System.Drawing.Point(12, 10)
$grpConfig.Size = New-Object System.Drawing.Size(1018, 160)
$form.Controls.Add($grpConfig)

$grpConfig.Controls.Add((New-Label '电机型号' 18 30 80))
$cmbMotor = New-Combo 100 30 150
[void]$cmbMotor.Items.Add('M3508 + C620')
[void]$cmbMotor.Items.Add('M2006 + C610')
$grpConfig.Controls.Add($cmbMotor)

$grpConfig.Controls.Add((New-Label '电调 ID' 280 30 70))
$numId = New-Object System.Windows.Forms.NumericUpDown
$numId.Location = New-Object System.Drawing.Point(350, 30)
$numId.Size = New-Object System.Drawing.Size(70, 25)
$numId.Minimum = 1
$numId.Maximum = 8
$grpConfig.Controls.Add($numId)

$grpConfig.Controls.Add((New-Label 'CAN 接口' 455 30 75))
$cmbCan = New-Combo 530 30 150
[void]$cmbCan.Items.Add('CAN1（2Pin）')
[void]$cmbCan.Items.Add('CAN2（4Pin）')
$grpConfig.Controls.Add($cmbCan)

$grpConfig.Controls.Add((New-Label 'C板串口' 710 30 75))
$cmbBoardUart = New-Combo 785 30 205
[void]$cmbBoardUart.Items.Add('串口1 → USART6 PG14/PG9')
[void]$cmbBoardUart.Items.Add('串口2 → USART1 PA9/PB7')
$grpConfig.Controls.Add($cmbBoardUart)

$grpConfig.Controls.Add((New-Label '默认协议' 18 76 80))
$cmbDefaultFormat = New-Combo 100 76 150
[void]$cmbDefaultFormat.Items.Add('FireWater')
[void]$cmbDefaultFormat.Items.Add('JustFloat')
$grpConfig.Controls.Add($cmbDefaultFormat)

$btnSave = New-Button '保存固件配置' 280 73 140
$grpConfig.Controls.Add($btnSave)
$btnKeil = New-Button '保存并打开 Keil' 455 73 155
$grpConfig.Controls.Add($btnKeil)
$btnBuild = New-Button 'Keil 一键编译' 645 73 140
$grpConfig.Controls.Add($btnBuild)

$lblConfigState = New-Label '' 18 118 970
$lblConfigState.ForeColor = [System.Drawing.Color]::DarkGreen
$grpConfig.Controls.Add($lblConfigState)

$grpSerial = New-Object System.Windows.Forms.GroupBox
$grpSerial.Text = '2. 内置串口调试（烧录后使用）'
$grpSerial.Location = New-Object System.Drawing.Point(12, 178)
$grpSerial.Size = New-Object System.Drawing.Size(1018, 178)
$form.Controls.Add($grpSerial)

$grpSerial.Controls.Add((New-Label '电脑 COM' 18 30 80))
$cmbCom = New-Combo 100 30 105
$grpSerial.Controls.Add($cmbCom)
$btnRefresh = New-Button '刷新' 215 27 65
$grpSerial.Controls.Add($btnRefresh)
$btnConnect = New-Button '连接' 290 27 75
$grpSerial.Controls.Add($btnConnect)
$grpSerial.Controls.Add((New-Label '解析格式' 390 30 75))
$cmbMonitorFormat = New-Combo 465 30 135
[void]$cmbMonitorFormat.Items.Add('FireWater')
[void]$cmbMonitorFormat.Items.Add('JustFloat')
$grpSerial.Controls.Add($cmbMonitorFormat)
$btnApplyFormat = New-Button '切换设备格式' 615 27 120
$grpSerial.Controls.Add($btnApplyFormat)

$btnStop = New-Button 'STOP 急停' 18 72 105
$btnStop.BackColor = [System.Drawing.Color]::MistyRose
$grpSerial.Controls.Add($btnStop)
$btnRun = New-Button 'RUN 使能' 132 72 105
$btnRun.BackColor = [System.Drawing.Color]::Honeydew
$grpSerial.Controls.Add($btnRun)
$btnZero = New-Button 'ZERO 置零' 246 72 105
$grpSerial.Controls.Add($btnZero)
$btnSpeedMode = New-Button '速度模式' 360 72 105
$grpSerial.Controls.Add($btnSpeedMode)
$btnPositionMode = New-Button '位置模式' 474 72 105
$grpSerial.Controls.Add($btnPositionMode)
$btnHelp = New-Button 'HELP' 588 72 80
$grpSerial.Controls.Add($btnHelp)

$grpSerial.Controls.Add((New-Label '目标值' 18 118 60))
$txtTarget = New-Object System.Windows.Forms.TextBox
$txtTarget.Location = New-Object System.Drawing.Point(80, 118)
$txtTarget.Size = New-Object System.Drawing.Size(90, 25)
$txtTarget.Text = '200'
$grpSerial.Controls.Add($txtTarget)
$btnSetSpeed = New-Button '设置速度 rpm' 180 115 115
$grpSerial.Controls.Add($btnSetSpeed)
$btnSetPosition = New-Button '设置位置 °' 305 115 105
$grpSerial.Controls.Add($btnSetPosition)
$txtRaw = New-Object System.Windows.Forms.TextBox
$txtRaw.Location = New-Object System.Drawing.Point(435, 118)
$txtRaw.Size = New-Object System.Drawing.Size(360, 25)
$txtRaw.Text = 'PID:SPEED,1.0,2.0,0.0'
$grpSerial.Controls.Add($txtRaw)
$btnRaw = New-Button '发送命令' 805 115 100
$grpSerial.Controls.Add($btnRaw)
$btnClear = New-Button '清空日志' 915 115 80
$grpSerial.Controls.Add($btnClear)

$grpLog = New-Object System.Windows.Forms.GroupBox
$grpLog.Text = '3. 串口接收与状态'
$grpLog.Location = New-Object System.Drawing.Point(12, 364)
$grpLog.Size = New-Object System.Drawing.Size(1018, 345)
$form.Controls.Add($grpLog)

$txtLog = New-Object System.Windows.Forms.TextBox
$txtLog.Location = New-Object System.Drawing.Point(12, 25)
$txtLog.Size = New-Object System.Drawing.Size(994, 305)
$txtLog.Multiline = $true
$txtLog.ScrollBars = [System.Windows.Forms.ScrollBars]::Vertical
$txtLog.ReadOnly = $true
$txtLog.Font = New-Object System.Drawing.Font('Consolas', 9)
$grpLog.Controls.Add($txtLog)

function Load-Configuration {
    try {
        $text = [System.IO.File]::ReadAllText($script:ConfigPath)
        $cmbMotor.SelectedIndex = if ((Get-ConfigValue $text 'APP_MOTOR_TYPE') -eq 'MOTOR_TYPE_M2006') { 1 } else { 0 }
        $id = Get-ConfigValue $text 'APP_MOTOR_ID'
        $numId.Value = [decimal]([int]($id -replace '[^0-9]', ''))
        $cmbCan.SelectedIndex = if ((Get-ConfigValue $text 'APP_USE_CAN1') -eq '0U') { 1 } else { 0 }
        $cmbBoardUart.SelectedIndex = if ((Get-ConfigValue $text 'APP_USE_USART6') -eq '0U') { 1 } else { 0 }
        $cmbDefaultFormat.SelectedIndex = if ((Get-ConfigValue $text 'APP_DEFAULT_VOFA_FORMAT') -eq 'APP_VOFA_FORMAT_JUSTFLOAT') { 1 } else { 0 }
        $cmbMonitorFormat.SelectedIndex = $cmbDefaultFormat.SelectedIndex
        $lblConfigState.Text = "配置文件：$($script:ConfigPath)"
    } catch {
        $lblConfigState.ForeColor = [System.Drawing.Color]::DarkRed
        $lblConfigState.Text = "读取配置失败：$($_.Exception.Message)"
    }
}

function Save-Configuration {
    try {
        $text = [System.IO.File]::ReadAllText($script:ConfigPath)
        $text = Set-ConfigDefine $text 'APP_MOTOR_TYPE' $(if ($cmbMotor.SelectedIndex -eq 1) { 'MOTOR_TYPE_M2006' } else { 'MOTOR_TYPE_M3508' })
        $text = Set-ConfigDefine $text 'APP_MOTOR_ID' ($numId.Value.ToString('0') + 'U')
        $text = Set-ConfigDefine $text 'APP_USE_CAN1' $(if ($cmbCan.SelectedIndex -eq 0) { '1U' } else { '0U' })
        $text = Set-ConfigDefine $text 'APP_USE_USART6' $(if ($cmbBoardUart.SelectedIndex -eq 0) { '1U' } else { '0U' })
        $text = Set-ConfigDefine $text 'APP_DEFAULT_VOFA_FORMAT' $(if ($cmbDefaultFormat.SelectedIndex -eq 1) { 'APP_VOFA_FORMAT_JUSTFLOAT' } else { 'APP_VOFA_FORMAT_FIREWATER' })
        Write-Utf8NoBom $script:ConfigPath $text
        $lblConfigState.ForeColor = [System.Drawing.Color]::DarkGreen
        $lblConfigState.Text = '配置已保存。请重新编译并烧录后生效。'
        Add-Log '固件配置已保存。'
        return $true
    } catch {
        [System.Windows.Forms.MessageBox]::Show("保存失败：$($_.Exception.Message)", '错误') | Out-Null
        return $false
    }
}

function Find-Keil {
    $settingsDirectory = Join-Path $env:LOCALAPPDATA 'CBoardMotorTool'
    $settingsFile = Join-Path $settingsDirectory 'keil_path.txt'
    $candidates = New-Object 'System.Collections.Generic.List[string]'

    if (-not [string]::IsNullOrWhiteSpace($env:KEIL_UVISION_PATH)) {
        $candidates.Add($env:KEIL_UVISION_PATH)
    }
    if (Test-Path -LiteralPath $settingsFile) {
        $savedPath = ([System.IO.File]::ReadAllText($settingsFile)).Trim()
        if ($savedPath) { $candidates.Add($savedPath) }
    }
    $pathCommand = Get-Command UV4.exe -ErrorAction SilentlyContinue
    if ($null -ne $pathCommand) { $candidates.Add($pathCommand.Source) }

    $registryKeys = @(
        'HKLM:\SOFTWARE\WOW6432Node\Keil\Products\MDK',
        'HKLM:\SOFTWARE\Keil\Products\MDK',
        'HKCU:\SOFTWARE\Keil\Products\MDK'
    )
    foreach ($key in $registryKeys) {
        if (Test-Path $key) {
            $properties = Get-ItemProperty $key -ErrorAction SilentlyContinue
            foreach ($propertyName in @('Path', 'InstallPath', 'Root')) {
                $root = $properties.$propertyName
                if ($root) {
                    $candidates.Add((Join-Path $root 'UV4\UV4.exe'))
                    $candidates.Add((Join-Path (Split-Path $root -Parent) 'UV4\UV4.exe'))
                }
            }
        }
    }

    $uninstallRoots = @(
        'HKLM:\SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall\*',
        'HKLM:\SOFTWARE\WOW6432Node\Microsoft\Windows\CurrentVersion\Uninstall\*',
        'HKCU:\SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall\*'
    )
    foreach ($root in $uninstallRoots) {
        Get-ItemProperty $root -ErrorAction SilentlyContinue |
            Where-Object { $_.DisplayName -match 'Keil|MDK-ARM|uVision' } |
            ForEach-Object {
                if ($_.InstallLocation) { $candidates.Add((Join-Path $_.InstallLocation 'UV4\UV4.exe')) }
                if ($_.DisplayIcon) { $candidates.Add(($_.DisplayIcon -replace ',\d+$', '').Trim('"')) }
            }
    }

    foreach ($drive in [System.IO.DriveInfo]::GetDrives()) {
        if ($drive.IsReady) {
            foreach ($relative in @('Keil_v5\UV4\UV4.exe', 'Keil5\UV4\UV4.exe',
                                    'Keil\UV4\UV4.exe', 'MDK-ARM\UV4\UV4.exe')) {
                $candidates.Add((Join-Path $drive.RootDirectory.FullName $relative))
            }
        }
    }

    foreach ($path in $candidates) {
        if ($path -and (Test-Path -LiteralPath $path -PathType Leaf)) {
            try {
                [System.IO.Directory]::CreateDirectory($settingsDirectory) | Out-Null
                [System.IO.File]::WriteAllText($settingsFile, $path)
            } catch {}
            return $path
        }
    }

    $selected = New-Object System.Windows.Forms.OpenFileDialog
    $selected.Filter = 'Keil uVision (UV4.exe)|UV4.exe'
    $selected.Title = '未自动找到 Keil，请选择任意安装路径下的 UV4.exe（只需选择一次）'
    if ($selected.ShowDialog() -eq [System.Windows.Forms.DialogResult]::OK) {
        try {
            [System.IO.Directory]::CreateDirectory($settingsDirectory) | Out-Null
            [System.IO.File]::WriteAllText($settingsFile, $selected.FileName)
        } catch {}
        return $selected.FileName
    }
    return $null
}

$btnSave.Add_Click({ [void](Save-Configuration) })
$btnKeil.Add_Click({
    if (Save-Configuration) {
        $keil = Find-Keil
        if ($null -ne $keil) { Start-Process -FilePath $keil -ArgumentList @($script:KeilProject) }
    }
})
$btnBuild.Add_Click({
    if (Save-Configuration) {
        $keil = Find-Keil
        if ($null -ne $keil) {
            $buildLog = Join-Path $script:ProjectRoot 'MDK-ARM\gui_build.log'
            Add-Log '开始 Keil Rebuild...'
            $process = Start-Process -FilePath $keil -ArgumentList @('-b', $script:KeilProject, '-j0', '-o', $buildLog) -WindowStyle Hidden -Wait -PassThru
            if (Test-Path -LiteralPath $buildLog) {
                $result = [System.IO.File]::ReadAllText($buildLog)
                if ($result -match '0 Error\(s\)') { Add-Log 'Keil 编译成功：0 Error(s)。' }
                else { Add-Log "Keil 编译失败，请查看：$buildLog" }
            } else { Add-Log "Keil 退出代码：$($process.ExitCode)" }
        }
    }
})

$btnRefresh.Add_Click({ Refresh-ComPorts })
$btnConnect.Add_Click({
    if ($null -ne $script:Serial -and $script:Serial.IsOpen) {
        $script:Serial.Close()
        $script:Serial.Dispose()
        $script:Serial = $null
        $btnConnect.Text = '连接'
        Add-Log '串口已断开。'
        return
    }
    if ([string]::IsNullOrWhiteSpace($cmbCom.Text)) { return }
    try {
        $script:Serial = New-Object System.IO.Ports.SerialPort
        $script:Serial.PortName = $cmbCom.Text
        $script:Serial.BaudRate = 115200
        $script:Serial.DataBits = 8
        $script:Serial.Parity = [System.IO.Ports.Parity]::None
        $script:Serial.StopBits = [System.IO.Ports.StopBits]::One
        $script:Serial.Handshake = [System.IO.Ports.Handshake]::None
        $script:Serial.ReadTimeout = 20
        $script:Serial.WriteTimeout = 200
        $script:Serial.Open()
        $btnConnect.Text = '断开'
        Add-Log "已连接 $($cmbCom.Text)，115200-8-N-1。"
    } catch {
        if ($null -ne $script:Serial) { $script:Serial.Dispose(); $script:Serial = $null }
        [System.Windows.Forms.MessageBox]::Show("串口打开失败：$($_.Exception.Message)", '错误') | Out-Null
    }
})

$btnStop.Add_Click({ Send-Command 'STOP' })
$btnRun.Add_Click({ Send-Command 'RUN' })
$btnZero.Add_Click({ Send-Command 'ZERO' })
$btnSpeedMode.Add_Click({ Send-Command 'MODE:SPEED' })
$btnPositionMode.Add_Click({ Send-Command 'MODE:POSITION' })
$btnHelp.Add_Click({ Send-Command 'HELP' })
$btnSetSpeed.Add_Click({ Send-Command ('SPEED:' + $txtTarget.Text.Trim()) })
$btnSetPosition.Add_Click({ Send-Command ('POS:' + $txtTarget.Text.Trim()) })
$btnRaw.Add_Click({ Send-Command $txtRaw.Text.Trim() })
$btnClear.Add_Click({ $txtLog.Clear() })
$btnApplyFormat.Add_Click({
    if ($cmbMonitorFormat.SelectedIndex -eq 1) { Send-Command 'FORMAT:JUSTFLOAT' }
    else { Send-Command 'FORMAT:FIREWATER' }
    $script:RxBinary.Clear()
})

$serialTimer = New-Object System.Windows.Forms.Timer
$serialTimer.Interval = 50
$serialTimer.Add_Tick({
    if ($null -eq $script:Serial -or -not $script:Serial.IsOpen) { return }
    try {
        $count = $script:Serial.BytesToRead
        if ($count -le 0) { return }
        $data = New-Object byte[] $count
        [void]$script:Serial.Read($data, 0, $count)
        if ($cmbMonitorFormat.SelectedIndex -eq 1) {
            foreach ($byte in $data) { $script:RxBinary.Add($byte) }
            Decode-JustFloat
        } else {
            $text = [System.Text.Encoding]::ASCII.GetString($data)
            $txtLog.AppendText($text)
            $txtLog.SelectionStart = $txtLog.TextLength
            $txtLog.ScrollToCaret()
        }
    } catch {
        Add-Log "串口读取错误：$($_.Exception.Message)"
    }
})
$serialTimer.Start()

$form.Add_FormClosing({
    $script:IsClosing = $true
    $serialTimer.Stop()
    if ($null -ne $script:Serial) {
        if ($script:Serial.IsOpen) { $script:Serial.Close() }
        $script:Serial.Dispose()
    }
})

Load-Configuration
Refresh-ComPorts
Add-Log 'C板单电机测试台已启动：仅用于一台 M2006+C610 或 M3508+C620。'
Add-Log '首次测试请架空电机，先 STOP，设置 100~200 rpm，再 RUN。'
if ($SelfTest) {
    Write-Output 'CBoard Motor GUI self-test OK'
    $serialTimer.Stop()
    $form.Dispose()
    exit 0
}
[void]$form.ShowDialog()
