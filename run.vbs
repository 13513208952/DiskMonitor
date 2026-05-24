Set sh = CreateObject("WScript.Shell")
sh.Run """" & CreateObject("Scripting.FileSystemObject").GetParentFolderName(WScript.ScriptFullName) & "\DiskMonitor.Frontend\bin\Debug\net9.0-windows\DiskMonitor.Frontend.exe""", 0, False
