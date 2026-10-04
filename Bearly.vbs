' Bearly launcher (desktop shortcut target).
' Runs the elevated "Bearly" scheduled task so there is no UAC prompt after first launch.
' First launch: asks for admin once, registers the task, then opens Bearly.
Option Explicit
Dim sh, fso, dir, rc
Set sh = CreateObject("WScript.Shell")
Set fso = CreateObject("Scripting.FileSystemObject")
dir = fso.GetParentFolderName(WScript.ScriptFullName)

On Error Resume Next
rc = sh.Run("schtasks.exe /run /tn ""Bearly""", 0, True)
On Error GoTo 0

If rc <> 0 Then
    CreateObject("Shell.Application").ShellExecute "powershell.exe", _
        "-NoProfile -ExecutionPolicy Bypass -WindowStyle Hidden -File """ & dir & "\Bearly.ps1"" -Install", _
        dir, "runas", 0
End If
