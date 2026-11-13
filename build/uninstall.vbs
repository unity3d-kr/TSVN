' Removes the TSVN extension without opening a window.
'
' install-vsix.py --uninstall does the work: it moves the installed folder into
' %LOCALAPPDATA%\TSVN\backups, so it can be put back by hand, and clears the
' caches that would otherwise keep the old composition alive. It starts no child
' process, and this file runs the work under pythonw.exe, the GUI interpreter, so
' there is no console to inherit and nothing appears on screen.
'
' Removing the extension from Visual Studio's Extension Manager instead is what
' shows a black window: Microsoft's installer launches a second copy of itself
' from the Visual Studio Installer directory, and that copy is a console program,
' which no switch suppresses.
'
' A successful run is silent; the report is in uninstall.log next to this file.
' A failure shows one message box with the end of that report, because the window
' that used to carry the error is exactly the thing being removed.
'
' Double-click this file, or from a terminal:
'     cscript //nologo build\uninstall.vbs
'     echo %ERRORLEVEL%

Option Explicit

Const ScriptName = "install-vsix.py"
Const LogName = "uninstall.log"
Const TailLines = 12
Const LineWidth = 100

Dim shell, fso, here, scriptPath, logPath, code

Set shell = CreateObject("WScript.Shell")
Set fso = CreateObject("Scripting.FileSystemObject")

here = fso.GetParentFolderName(WScript.ScriptFullName)
scriptPath = fso.BuildPath(here, ScriptName)
logPath = fso.BuildPath(here, LogName)

If Not fso.FileExists(scriptPath) Then
    shell.Popup "Not found: " & scriptPath, 0, "TSVN - remove failed", 16
    WScript.Quit 2
End If

code = Launch()

If code <> 0 Then
    shell.Popup Report(code), 0, "TSVN - remove failed", 48
End If

' So a caller can branch on the result: a script otherwise always reports success.
WScript.Quit code

Function Launch()
    Dim names, index, command, result, arguments

    arguments = " --uninstall --log " & Chr(34) & logPath & Chr(34)

    ' pythonw.exe first - no console at all. python.exe is the fallback for an
    ' installation that ships only the console interpreter.
    names = Array("pythonw.exe", "python.exe")

    For index = 0 To UBound(names)
        command = Chr(34) & names(index) & Chr(34) & " " & Chr(34) & scriptPath & Chr(34) & arguments

        Err.Clear
        On Error Resume Next

        result = shell.Run(command, 0, True)

        If Err.Number = 0 Then
            Launch = result
            Exit Function
        End If

        On Error GoTo 0
    Next

    shell.Popup "Python was not found on PATH." & vbCrLf & vbCrLf _
        & "Install Python 3, or run uninstall.cmd from a terminal to see what fails.", _
        0, "TSVN - remove failed", 16

    Launch = 9009
End Function

Function Report(resultCode)
    Dim text, tail

    text = "The extension could not be removed (exit " & resultCode & ")." & vbCrLf & vbCrLf _
        & "Nothing was changed. The version already in Visual Studio is untouched." _
        & vbCrLf & vbCrLf _
        & "If Visual Studio is running, close it and run this again."

    tail = Tail(logPath)

    If Len(tail) > 0 Then
        text = text & vbCrLf & vbCrLf & tail
    End If

    Report = text
End Function

Function Tail(path)
    Dim stream, data, lines, first, index, text, line

    Tail = ""

    If Not fso.FileExists(path) Then
        Exit Function
    End If

    Set stream = fso.OpenTextFile(path, 1)

    data = stream.ReadAll

    stream.Close

    lines = Split(Replace(data, vbCrLf, vbLf), vbLf)
    first = UBound(lines) - TailLines + 1

    If first < 0 Then
        first = 0
    End If

    For index = first To UBound(lines)
        line = Trim(lines(index))

        If Len(line) > LineWidth Then
            line = Left(line, LineWidth) & "..."
        End If

        If Len(line) > 0 Then
            text = text & line & vbCrLf
        End If
    Next

    Tail = text
End Function
