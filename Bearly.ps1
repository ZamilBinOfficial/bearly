param([switch]$Install, [switch]$Validate)
# Bearly - one click Zero Mode + Deep Clean. Keep this file ASCII-only (PS 5.1 reads it as ANSI).
$ErrorActionPreference = 'Stop'
$Root = $PSScriptRoot

Add-Type -AssemblyName PresentationFramework, PresentationCore, WindowsBase, System.ServiceProcess

$isAdmin = ([Security.Principal.WindowsPrincipal][Security.Principal.WindowsIdentity]::GetCurrent()).IsInRole(
    [Security.Principal.WindowsBuiltInRole]::Administrator)

function Register-BearlyTask {
    $vbs = Join-Path $Root 'core\run-hidden.vbs'
    $action = New-ScheduledTaskAction -Execute "$env:WINDIR\System32\wscript.exe" -Argument "`"$vbs`""
    $principal = New-ScheduledTaskPrincipal -UserId "$env:USERDOMAIN\$env:USERNAME" -LogonType Interactive -RunLevel Highest
    $settings = New-ScheduledTaskSettingsSet -AllowStartIfOnBatteries -DontStopIfGoingOnBatteries `
        -ExecutionTimeLimit ([TimeSpan]::Zero) -MultipleInstances Parallel
    Register-ScheduledTask -TaskName 'Bearly' -Action $action -Principal $principal -Settings $settings -Force | Out-Null
}

try {
    if ($Install -and $isAdmin) { try { Register-BearlyTask } catch { } }

    if (-not $Validate) {
        $createdNew = $false
        $script:mutex = New-Object System.Threading.Mutex($true, 'Local\BearlyApp', [ref]$createdNew)
        if (-not $createdNew) { exit }
    }

    # Auto-unblock all files in root to clear Windows Mark-of-the-Web (Zone.Identifier 0x80131515)
    try { Get-ChildItem -Path $Root -Recurse | Unblock-File -ErrorAction SilentlyContinue } catch { }

    # ---------------------------------------------------------------- core engine (cached dll)
    $cs = Join-Path $Root 'core\BearlyCore.cs'
    $dll = Join-Path $Root 'core\bin\BearlyCore.dll'
    $svcRef = [System.ServiceProcess.ServiceController].Assembly.Location

    if (-not ('Bearly.Engine' -as [type])) {
        if (Test-Path $dll) {
            try { Unblock-File -Path $dll -ErrorAction SilentlyContinue } catch { }
            try {
                Add-Type -Path $dll -ErrorAction Stop
            } catch {
                # If blocked by CAS policy, load raw bytes directly
                try {
                    [System.Reflection.Assembly]::Load([System.IO.File]::ReadAllBytes($dll)) | Out-Null
                } catch { }
            }
        }

        # If still not loaded or stale, compile locally on this PC (bypasses all web blocks)
        if (-not ('Bearly.Engine' -as [type])) {
            New-Item -ItemType Directory -Force -Path (Split-Path $dll) | Out-Null
            try {
                Add-Type -Path $cs -ReferencedAssemblies $svcRef -OutputAssembly $dll -OutputType Library -ErrorAction Stop
            } catch {
                if (-not ('Bearly.Engine' -as [type])) { Add-Type -Path $cs -ReferencedAssemblies $svcRef }
            }
        }
    }
    [Bearly.Engine]::SetAppId('Zamil.Bearly')

    # ---------------------------------------------------------------- config
    $cfg = Get-Content -Raw -Path (Join-Path $Root 'config.json') | ConvertFrom-Json
    $kill = New-Object System.Collections.Generic.List[string]
    foreach ($grp in $cfg.killProcesses.PSObject.Properties) { foreach ($n in @($grp.Value)) { $kill.Add([string]$n) } }

    $engine = New-Object Bearly.Engine
    $engine.KillList = $kill.ToArray()
    $engine.ProtectList = [string[]]@($cfg.protectProcesses)
    $engine.ServiceList = [string[]]@($cfg.stopServices)
    $engine.ExtraCleanPaths = [string[]]@($cfg.extraCleanPaths | Where-Object { $_ })
    $engine.IsAdmin = $isAdmin

    # ---------------------------------------------------------------- UI (Boutique Human-Crafted)
    $xaml = @'
<Window xmlns="http://schemas.microsoft.com/winfx/2006/xaml/presentation"
        xmlns:x="http://schemas.microsoft.com/winfx/2006/xaml"
        Title="Bearly" Width="360" SizeToContent="Height"
        WindowStyle="None" AllowsTransparency="True" Background="Transparent"
        ResizeMode="NoResize" WindowStartupLocation="CenterScreen"
        FontFamily="Segoe UI Variable Text, Segoe UI, -apple-system, sans-serif" UseLayoutRounding="True"
        TextOptions.TextFormattingMode="Ideal" SnapsToDevicePixels="True">
  <Window.Resources>
    <SolidColorBrush x:Key="Bg" Color="#111113"/>
    <SolidColorBrush x:Key="Border" Color="#222226"/>
    <SolidColorBrush x:Key="CardBg" Color="#18181C"/>
    <SolidColorBrush x:Key="CardBorder" Color="#282830"/>
    <SolidColorBrush x:Key="Txt1" Color="#F4F4F6"/>
    <SolidColorBrush x:Key="Txt2" Color="#8E8E96"/>
    <SolidColorBrush x:Key="Txt3" Color="#54545C"/>
    <SolidColorBrush x:Key="GlyphBg" Color="#202026"/>
    <SolidColorBrush x:Key="Green" Color="#30D158"/>

    <!-- Native Window Button -->
    <Style x:Key="SysBtn" TargetType="Button">
      <Setter Property="Focusable" Value="False"/>
      <Setter Property="Cursor" Value="Hand"/>
      <Setter Property="Template">
        <Setter.Value>
          <ControlTemplate TargetType="Button">
            <Border x:Name="b" Width="26" Height="26" CornerRadius="6" Background="Transparent">
              <ContentPresenter HorizontalAlignment="Center" VerticalAlignment="Center"/>
            </Border>
            <ControlTemplate.Triggers>
              <Trigger Property="IsMouseOver" Value="True"><Setter TargetName="b" Property="Background" Value="#222228"/></Trigger>
            </ControlTemplate.Triggers>
          </ControlTemplate>
        </Setter.Value>
      </Setter>
    </Style>

    <!-- Boutique Action Card -->
    <Style x:Key="ActionCard" TargetType="Button">
      <Setter Property="Cursor" Value="Hand"/>
      <Setter Property="Focusable" Value="False"/>
      <Setter Property="HorizontalContentAlignment" Value="Stretch"/>
      <Setter Property="Template">
        <Setter.Value>
          <ControlTemplate TargetType="Button">
            <Border x:Name="bd" CornerRadius="10" BorderThickness="1" Padding="14,13"
                    Background="{StaticResource CardBg}" BorderBrush="{StaticResource CardBorder}">
              <ContentPresenter/>
            </Border>
            <ControlTemplate.Triggers>
              <Trigger Property="IsMouseOver" Value="True">
                <Setter TargetName="bd" Property="Background" Value="#1E1E24"/>
                <Setter TargetName="bd" Property="BorderBrush" Value="#383844"/>
              </Trigger>
              <Trigger Property="IsPressed" Value="True">
                <Setter TargetName="bd" Property="Background" Value="#151518"/>
                <Setter TargetName="bd" Property="Opacity" Value="0.85"/>
              </Trigger>
              <Trigger Property="IsEnabled" Value="False">
                <Setter TargetName="bd" Property="Opacity" Value="0.4"/>
              </Trigger>
            </ControlTemplate.Triggers>
          </ControlTemplate>
        </Setter.Value>
      </Setter>
    </Style>
  </Window.Resources>

  <Grid x:Name="Shell" Margin="14">
    <!-- Drop shadow -->
    <Border CornerRadius="12" Background="#000000" Margin="2">
      <Border.Effect><DropShadowEffect BlurRadius="22" ShadowDepth="4" Direction="270" Opacity="0.6" Color="#000000"/></Border.Effect>
    </Border>

    <!-- Main Container -->
    <Border CornerRadius="12" Background="{StaticResource Bg}" BorderBrush="{StaticResource Border}" BorderThickness="1">
      <StackPanel Margin="18,14,18,16">

        <!-- Header -->
        <Grid Height="28">
          <StackPanel Orientation="Horizontal" VerticalAlignment="Center">
            <Border Width="18" Height="18" CornerRadius="4" ClipToBounds="True">
              <Image x:Name="HeroLogo" Width="18" Height="18" RenderOptions.BitmapScalingMode="HighQuality"/>
            </Border>
            <TextBlock Text="Bearly" Margin="8,0,0,0" FontSize="12.5" FontWeight="SemiBold"
                       Foreground="{StaticResource Txt2}" VerticalAlignment="Center"/>
          </StackPanel>
          <StackPanel Orientation="Horizontal" HorizontalAlignment="Right" VerticalAlignment="Center">
            <Button x:Name="BtnMin" Style="{StaticResource SysBtn}">
              <Viewbox Width="9" Height="9"><Canvas Width="24" Height="24">
                <Path Data="M4,12 L20,12" Stroke="#787882" StrokeThickness="2.2" StrokeStartLineCap="Round" StrokeEndLineCap="Round"/>
              </Canvas></Viewbox>
            </Button>
            <Button x:Name="BtnClose" Style="{StaticResource SysBtn}" Margin="2,0,0,0">
              <Viewbox Width="9" Height="9"><Canvas Width="24" Height="24">
                <Path Data="M5,5 L19,19 M19,5 L5,19" Stroke="#787882" StrokeThickness="2.2" StrokeStartLineCap="Round" StrokeEndLineCap="Round"/>
              </Canvas></Viewbox>
            </Button>
          </StackPanel>
        </Grid>

        <!-- Live Free Memory & Disk Pill (Only Free Resources) -->
        <Border Margin="0,14,0,0" Padding="12,8" CornerRadius="8" Background="#151518" BorderBrush="#202026" BorderThickness="1">
          <Grid>
            <StackPanel Orientation="Horizontal" VerticalAlignment="Center">
              <Ellipse Width="6" Height="6" Fill="{StaticResource Green}" Margin="0,0,8,0" VerticalAlignment="Center"/>
              <TextBlock x:Name="StatRam" Text="-- GB RAM free" FontSize="11.5" FontWeight="SemiBold" Foreground="{StaticResource Txt1}"/>
            </StackPanel>
            <TextBlock x:Name="StatDisk" Text="-- GB disk free" HorizontalAlignment="Right"
                       FontSize="11" Foreground="{StaticResource Txt3}" VerticalAlignment="Center"/>
          </Grid>
        </Border>

        <!-- Two Core Actions -->
        <StackPanel Margin="0,10,0,0">
          <!-- 1. Zero Mode -->
          <Button x:Name="BtnZero" Style="{StaticResource ActionCard}">
            <Grid>
              <Grid.ColumnDefinitions>
                <ColumnDefinition Width="Auto"/>
                <ColumnDefinition Width="*"/>
                <ColumnDefinition Width="Auto"/>
              </Grid.ColumnDefinitions>

              <!-- Power Icon -->
              <Border Width="34" Height="34" CornerRadius="8" Background="{StaticResource GlyphBg}" VerticalAlignment="Center">
                <Viewbox Width="15" Height="15"><Canvas Width="24" Height="24">
                  <Path Data="M12,3.5 L12,11" Stroke="#F4F4F6" StrokeThickness="2.2" StrokeStartLineCap="Round" StrokeEndLineCap="Round"/>
                  <Path Data="M6.5,6.5 A7.8,7.8 0 1 0 17.5,6.5" Stroke="#F4F4F6" StrokeThickness="2.2" StrokeStartLineCap="Round" StrokeEndLineCap="Round"/>
                </Canvas></Viewbox>
              </Border>

              <StackPanel Grid.Column="1" Margin="12,0,8,0" VerticalAlignment="Center">
                <TextBlock Text="Zero Mode" FontSize="14" FontWeight="SemiBold" Foreground="{StaticResource Txt1}"/>
                <TextBlock Text="Stop background apps &amp; flush RAM" FontSize="11" Foreground="{StaticResource Txt2}" Margin="0,2,0,0"/>
              </StackPanel>

              <Viewbox Grid.Column="2" Width="10" Height="10" VerticalAlignment="Center"><Canvas Width="24" Height="24">
                <Path Data="M9,5 L16,12 L9,19" Stroke="#5E5E68" StrokeThickness="2.4" StrokeStartLineCap="Round" StrokeEndLineCap="Round" StrokeLineJoin="Round"/>
              </Canvas></Viewbox>
            </Grid>
          </Button>

          <!-- 2. Deep Clean -->
          <Button x:Name="BtnClean" Style="{StaticResource ActionCard}" Margin="0,8,0,0">
            <Grid>
              <Grid.ColumnDefinitions>
                <ColumnDefinition Width="Auto"/>
                <ColumnDefinition Width="*"/>
                <ColumnDefinition Width="Auto"/>
              </Grid.ColumnDefinitions>

              <!-- Trash / Cache Icon -->
              <Border Width="34" Height="34" CornerRadius="8" Background="{StaticResource GlyphBg}" VerticalAlignment="Center">
                <Viewbox Width="15" Height="15"><Canvas Width="24" Height="24">
                  <Path Data="M4,7 L20,7 M9.5,7 L9.5,4.5 L14.5,4.5 L14.5,7 M6.5,7 L7.4,19.5 L16.6,19.5 L17.5,7"
                        Stroke="#F4F4F6" StrokeThickness="1.8" StrokeStartLineCap="Round" StrokeEndLineCap="Round" StrokeLineJoin="Round"/>
                  <Path Data="M10,11 L10,16 M14,11 L14,16"
                        Stroke="#F4F4F6" StrokeThickness="1.8" StrokeStartLineCap="Round" StrokeEndLineCap="Round"/>
                </Canvas></Viewbox>
              </Border>

              <StackPanel Grid.Column="1" Margin="12,0,8,0" VerticalAlignment="Center">
                <TextBlock Text="Deep Clean" FontSize="14" FontWeight="SemiBold" Foreground="{StaticResource Txt1}"/>
                <TextBlock Text="Vanish caches, temp &amp; junk files" FontSize="11" Foreground="{StaticResource Txt2}" Margin="0,2,0,0"/>
              </StackPanel>

              <Viewbox Grid.Column="2" Width="10" Height="10" VerticalAlignment="Center"><Canvas Width="24" Height="24">
                <Path Data="M9,5 L16,12 L9,19" Stroke="#5E5E68" StrokeThickness="2.4" StrokeStartLineCap="Round" StrokeEndLineCap="Round" StrokeLineJoin="Round"/>
              </Canvas></Viewbox>
            </Grid>
          </Button>
        </StackPanel>

        <!-- Status / Action Activity Area -->
        <Border x:Name="ActivityBox" Margin="0,10,0,0" Padding="12,10" CornerRadius="8"
                Background="#141417" BorderBrush="#1E1E24" BorderThickness="1">
          <Grid>
            <!-- Idle hint -->
            <StackPanel x:Name="IdleState">
              <TextBlock x:Name="StatusLine" Text="A PC restart wakes everything back up."
                         FontSize="11" Foreground="{StaticResource Txt3}"/>
            </StackPanel>

            <!-- Busy State (running action) -->
            <Grid x:Name="BusyState" Visibility="Collapsed">
              <StackPanel VerticalAlignment="Center">
                <StackPanel Orientation="Horizontal">
                  <Ellipse Width="5" Height="5" Fill="#60A5FA" Margin="0,0,7,0" VerticalAlignment="Center"/>
                  <TextBlock x:Name="BusyTitle" Text="Quieting background tasks..." FontSize="11.5" FontWeight="SemiBold" Foreground="{StaticResource Txt1}"/>
                </StackPanel>
                <TextBlock x:Name="BusyLog" Text="Starting..." FontSize="10.5" Foreground="{StaticResource Txt2}" Margin="12,2,0,0"/>
              </StackPanel>
              <TextBlock x:Name="BusyElapsed" Text="0.0s" HorizontalAlignment="Right" FontSize="10.5" Foreground="{StaticResource Txt3}" FontFamily="Cascadia Mono, Consolas" VerticalAlignment="Top"/>
            </Grid>

            <!-- Done State -->
            <StackPanel x:Name="DoneState" Visibility="Collapsed">
              <StackPanel Orientation="Horizontal">
                <TextBlock Text="✓" FontSize="11" FontWeight="Bold" Foreground="{StaticResource Green}" Margin="0,0,6,0" VerticalAlignment="Center"/>
                <TextBlock x:Name="DoneBig" Text="Freed 3.8 GB RAM" FontSize="12" FontWeight="SemiBold" Foreground="{StaticResource Txt1}" VerticalAlignment="Center"/>
              </StackPanel>
              <TextBlock x:Name="DoneSub" Text="PC in zero state" FontSize="10.5" Foreground="{StaticResource Txt2}" Margin="14,1,0,0"/>
            </StackPanel>
          </Grid>
        </Border>

        <!-- Subtle Footer -->
        <Grid Margin="2,10,2,0">
          <TextBlock Text="Roblox, OBS &amp; Hexa protected" FontSize="10" Foreground="{StaticResource Txt3}"/>
          <TextBlock x:Name="CfgLink" Text="Settings" HorizontalAlignment="Right" FontSize="10" Foreground="{StaticResource Txt3}" Cursor="Hand"/>
        </Grid>
      </StackPanel>
    </Border>
  </Grid>
</Window>
'@

    $win = [Windows.Markup.XamlReader]::Parse($xaml)
    $ui = @{}
    foreach ($n in 'BtnMin', 'BtnClose', 'HeroLogo', 'StatRam', 'StatDisk',
        'BtnZero', 'BtnClean', 'ActivityBox', 'IdleState', 'StatusLine',
        'BusyState', 'BusyTitle', 'BusyLog', 'BusyElapsed', 'DoneState', 'DoneBig', 'DoneSub', 'CfgLink') {
        $ui[$n] = $win.FindName($n)
        if ($null -eq $ui[$n]) { throw "UI element missing: $n" }
    }

    $logo = Join-Path $Root 'assets\logo-256.png'
    if (Test-Path $logo) {
        $bmp = New-Object System.Windows.Media.Imaging.BitmapImage
        $bmp.BeginInit(); $bmp.UriSource = New-Object Uri($logo); $bmp.CacheOption = 'OnLoad'; $bmp.EndInit(); $bmp.Freeze()
        $ui.HeroLogo.Source = $bmp
    }
    $ico = Join-Path $Root 'assets\bearly.ico'
    if (Test-Path $ico) { $win.Icon = [System.Windows.Media.Imaging.BitmapFrame]::Create((New-Object Uri($ico))) }

    if ($Validate) {
        $null = [Bearly.SysMonitor]::Read()
        Write-Output 'BEARLY_OK'
        exit 0
    }

    # ---------------------------------------------------------------- stats updater (Show ONLY free resources)
    function Update-Stats {
        $s = [Bearly.SysMonitor]::Read()
        $freeRam = [Math]::Max(0.0, ($s.RamTotalGB - $s.RamUsedGB))
        $ui.StatRam.Text = ('{0:0.0} GB RAM free' -f $freeRam)
        $ui.StatDisk.Text = ('{0:0} GB disk free' -f $s.DiskFreeGB)
    }

    $script:watching = $false
    $script:mode = ''
    $script:t0 = [DateTime]::Now

    function Start-Action([string]$mode) {
        if ($engine.Busy) { return }
        $ui.BtnZero.IsEnabled = $false
        $ui.BtnClean.IsEnabled = $false
        $ui.IdleState.Visibility = 'Collapsed'
        $ui.DoneState.Visibility = 'Collapsed'
        $ui.BusyState.Visibility = 'Visible'
        $ui.BusyElapsed.Text = '0.0s'
        $script:mode = $mode
        $script:t0 = [DateTime]::Now

        if ($mode -eq 'zero') {
            $ui.BusyTitle.Text = 'Activating Zero Mode...'
            $ui.BusyLog.Text = 'Stopping non-essential tasks...'
            $engine.StartZero()
        } else {
            $ui.BusyTitle.Text = 'Running Deep Clean...'
            $ui.BusyLog.Text = 'Flushing caches...'
            $engine.StartClean()
        }
        $script:watching = $true
    }

    function Complete-Action {
        $script:watching = $false
        $ui.BusyState.Visibility = 'Collapsed'
        $ui.DoneBig.Text = $engine.ResultBig
        $ui.DoneSub.Text = $engine.ResultDetail
        $ui.DoneState.Visibility = 'Visible'
        $ui.BtnZero.IsEnabled = $true
        $ui.BtnClean.IsEnabled = $true
        Update-Stats
        [GC]::Collect()
        [Bearly.Engine]::TrimSelf()
    }

    # ---------------------------------------------------------------- wiring
    $ui.BtnZero.Add_Click({ Start-Action 'zero' })
    $ui.BtnClean.Add_Click({ Start-Action 'clean' })
    $ui.BtnClose.Add_Click({ $win.Close() })
    $ui.BtnMin.Add_Click({ $win.WindowState = [System.Windows.WindowState]::Minimized })
    $win.Add_MouseLeftButtonDown({ try { $win.DragMove() } catch { } })
    $ui.CfgLink.Add_MouseEnter({ $ui.CfgLink.Foreground = [System.Windows.Media.Brushes]::White })
    $ui.CfgLink.Add_MouseLeave({ $ui.CfgLink.Foreground = [System.Windows.Media.BrushConverter]::new().ConvertFromString('#54545C') })
    $ui.CfgLink.Add_MouseLeftButtonDown({
            param($sender, $e)
            $e.Handled = $true
            Start-Process notepad.exe -ArgumentList "`"$(Join-Path $Root 'config.json')`""
        })

    $statsTimer = New-Object System.Windows.Threading.DispatcherTimer
    $statsTimer.Interval = [TimeSpan]::FromMilliseconds(1000)
    $statsTimer.Add_Tick({ Update-Stats })

    $logTimer = New-Object System.Windows.Threading.DispatcherTimer
    $logTimer.Interval = [TimeSpan]::FromMilliseconds(90)
    $logTimer.Add_Tick({
            $line = $null
            while ($engine.Log.TryDequeue([ref]$line)) {
                $i = $line.IndexOf('|')
                $msg = if ($i -gt 0) { $line.Substring($i + 1) } else { $line }
                if ($script:watching -and $msg) { $ui.BusyLog.Text = $msg }
            }
            if ($script:watching) {
                $ui.BusyElapsed.Text = ('{0:0.0}s' -f ([DateTime]::Now - $script:t0).TotalSeconds)
                if (-not $engine.Busy) {
                    Complete-Action
                }
            }
        })

    $win.Add_Loaded({
            Update-Stats
            $statsTimer.Start()
            $logTimer.Start()
        })
    $win.Add_Closed({
            $statsTimer.Stop()
            $logTimer.Stop()
            try { $script:mutex.ReleaseMutex() } catch { }
        })

    $null = $win.ShowDialog()
}
catch {
    $msg = $_.Exception.Message
    if ($Validate) { Write-Output "BEARLY_FAIL: $msg"; exit 1 }
    [System.Windows.MessageBox]::Show("Bearly could not start:`n`n$msg", 'Bearly') | Out-Null
}
