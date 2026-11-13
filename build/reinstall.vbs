' Builds and installs TSVN without opening a window.
'
' reinstall.py does the work. It is started through pythonw.exe, the GUI
' interpreter, so this process never owns a console and the children it spawns -
' resgen, dotnet, python - are given CREATE_NO_WINDOW, which means nothing appears
' on screen from start to finish. Launched as a .cmd instead, the same steps would
' get a console from Explorer and every child would inherit it.
'
' A run that succeeds is completely silent: the summary is in install.log. A run
' that fails shows one message box with the end of that log, because the window
' that used to carry the error is exactly the thing being removed.
'
' Double-click this file, or from a terminal:
'     cscript //nologo build\reinstall.vbs
'     echo %ERRORLEVEL%

Option Explicit

Const ScriptName = "reinstall.py"
Const LogName = "install.log"
Const TailLines = 12
Const LineWidth = 100

Dim shell, fso, here, scriptPath, logPath, code

Set shell = CreateObject("WScript.Shell")
Set fso = CreateObject("Scripting.FileSystemObject")

here = fso.GetParentFolderName(WScript.ScriptFullName)
scriptPath = fso.BuildPath(here, ScriptName)
logPath = fso.BuildPath(here, LogName)

If Not fso.FileExists(scriptPath) Then
    shell.Popup "Not found: " & scriptPath, 0, "TSVN - install failed", 16
    WScript.Quit 2
End If

code = Launch(scriptPath)

If code <> 0 Then
    shell.Popup Report(code, logPath), 0, "TSVN - install failed", 48
End If

' So a caller can branch on the result: a script otherwise always reports success.
WScript.Quit code

Function Launch(path)
    Dim names, index, command, result

    ' pythonw.exe first - no console at all. python.exe is the fallback for an
    ' installation that ships only the console interpreter.
    names = Array("pythonw.exe", "python.exe")

    For index = 0 To UBound(names)
        command = Chr(34) & names(index) & Chr(34) & " " & Chr(34) & path & Chr(34)

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
        & "Install Python 3, or run reinstall.cmd from a terminal to see what fails.", _
        0, "TSVN - install failed", 16

    Launch = 9009
End Function

Function Report(resultCode, logPath)
    Dim text, tail

    text = "Build or install failed (exit " & resultCode & ")." & vbCrLf & vbCrLf _
        & "Nothing was installed. The version already in Visual Studio is unchanged." _
        & vbCrLf & vbCrLf _
        & "If Visual Studio is running, close it and run this again."

    tail = Tail(logPath)

    If Len(tail) > 0 Then
        text = text & vbCrLf & vbCrLf & tail
    End If

    Report = text
End Function

Function Tail(logPath)
    Dim stream, data, lines, first, index, text, line

    Tail = ""

    If Not fso.FileExists(logPath) Then
        Exit Function
    End If

    Set stream = fso.OpenTextFile(logPath, 1)

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
