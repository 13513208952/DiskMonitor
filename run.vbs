Dim fso, root, exe, sh
Set fso  = CreateObject("Scripting.FileSystemObject")
Set sh   = CreateObject("WScript.Shell")

root = fso.GetParentFolderName(WScript.ScriptFullName)
exe  = root & "\DiskMonitor.Frontend\bin\Debug\net9.0-windows\DiskMonitor.Frontend.exe"

If Not fso.FileExists(exe) Then
    sh.Run "dotnet build """ & root & "\DiskMonitor.Frontend\DiskMonitor.Frontend.csproj"" -c Debug --nologo", 1, True
End If

sh.Run """" & exe & """", 0, False
