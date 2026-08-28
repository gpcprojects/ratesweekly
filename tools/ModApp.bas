Attribute VB_Name = "ModApp"
Option Explicit

' RatesWeekly app module. The incumbent's own macros and buttons are untouched:
' XX_upd_store still stores each bank exactly as before.
'
' 2026-08-28 fixes:
'   1. IsDate() on a cell still showing #N/A (a BDP that has not resolved) raises a type
'      mismatch, which broke STORE ALL + RUNS PAGE into the debugger. Every read of a
'      Bloomberg-fed cell now goes through SafeDate/SafeNum, which tolerate error values.
'   2. The Runs page was cleared BEFORE the rebuild, so a run that found nothing left an
'      empty sheet. It is now built off-sheet and only committed if at least one bank
'      produced rows; otherwise the previous page is left alone and the user is told.
'   3. Column order was Step then Priced. The desk order is Mid | Priced | Step everywhere.
'   4. The three change columns used to be copied out of Current!L:N. Those cells are not
'      formulas - they are literals the incumbent's macro stamps - so a workbook carried
'      whatever they held when the template was captured, and the page published 19-Aug
'      changes next to live prices. They are now COMPUTED from the Historical_ sheet this
'      workbook already maintains, matched on the meeting's own START DATE rather than on
'      a ticker position, so the answer survives a roll. Blank when there is no stored
'      observation near the anchor, which is the honest value and never a zero.

' Set True to run the rebuild without the closing message box - lets the build verify the
' module actually compiles and runs, which a modal MsgBox otherwise blocks forever.
Public SuppressPrompts As Boolean

Private Const TOL_1D As Long = 6         ' same walk-back caps the app's boards use
Private Const TOL_1W As Long = 7
Private Const TOL_1M As Long = 10

Private Function SafeDate(v As Variant) As Variant
    SafeDate = Empty
    If IsError(v) Then Exit Function
    If IsDate(v) Then SafeDate = CDate(v)
End Function

Private Function SafeNum(v As Variant) As Variant
    SafeNum = Empty
    If IsError(v) Then Exit Function
    If IsNumeric(v) Then SafeNum = CDbl(v)
End Function

' A..E of a Historical_ sheet, read once per bank: A=CurrentDate, C=StartDate, E=Rate.
Private Function LoadHist(sheetName As String) As Variant
    Dim ws As Worksheet, last As Long
    On Error Resume Next
    Set ws = ThisWorkbook.Worksheets(sheetName)
    On Error GoTo 0
    If ws Is Nothing Then
        LoadHist = Empty
        Exit Function
    End If
    last = ws.Cells(ws.Rows.Count, 1).End(xlUp).Row
    If last < 2 Then
        LoadHist = Empty
        Exit Function
    End If
    LoadHist = ws.Range("A2:E" & last).Value
End Function

' The rate this contract was quoted at on the latest stored day at-or-before hiDate and
' no earlier than loDate. Empty when nothing sits in that window - the caller then
' publishes blank rather than reaching further back and mislabelling the horizon.
Private Function RateOn(h As Variant, startDate As Variant, hiDate As Date, loDate As Date) As Variant
    Dim i As Long, best As Date, bestVal As Variant, d As Variant, s As Variant
    RateOn = Empty
    If IsEmpty(h) Then Exit Function
    If IsEmpty(startDate) Then Exit Function
    best = 0
    For i = 1 To UBound(h, 1)
        d = h(i, 1)
        s = h(i, 3)
        If IsDate(d) And IsDate(s) Then
            If CDate(s) = CDate(startDate) Then
                If CDate(d) <= hiDate And CDate(d) >= loDate Then
                    If CDate(d) > best And IsNumeric(h(i, 5)) Then
                        best = CDate(d)
                        bestVal = CDbl(h(i, 5))
                    End If
                End If
            End If
        End If
    Next i
    If best > 0 Then RateOn = bestVal
End Function

Private Function ChgBp(midVal As Variant, anchor As Variant) As Variant
    ChgBp = Empty
    If IsEmpty(midVal) Or IsEmpty(anchor) Then Exit Function
    ChgBp = (CDbl(midVal) - CDbl(anchor)) * 100#
End Function

Sub StoreAllRefresh()
    On Error Resume Next
    AU_upd_store
    NZ_upd_store
    EU_upd_store
    UK_upd_store
    US_upd_store
    CD_upd_store
    NOK_upd_store
    JPY_upd_store
    SEK_upd_store
    On Error GoTo 0
    RebuildRunsPage
End Sub

Sub RebuildRunsPage()
    Dim ws As Worksheet, tmp As Worksheet, cur As ListObject
    Dim names As Variant, labels As Variant, shts As Variant, hdr As Variant
    Dim b As Long, r As Long, i As Long, c As Long
    Dim banks As Long, rowsOut As Long
    Dim h As Variant, sd As Variant, md As Variant
    Dim t1 As Date, t7 As Date, t30 As Date

    names = Array("current_eu", "current_uk", "current_au", "current_nz", "current_us", _
                  "current_cd", "current_nok", "current_jpy", "current_sek")
    labels = Array("ECB", "MPC", "RBA", "RBNZ", "FOMC", "BOC", "NORGES", "BOJ", "RIKSBANK")
    shts = Array("Historical_EU", "Historical_UK", "Historical_AU", "Historical_NZ", _
                 "Historical_US", "Historical_CD", "Historical_NOK", "Historical_JPY", _
                 "Historical_SEK")
    hdr = Array("StartDate", "Maturity", "Mid", "Priced (bp)", "Step (bp)", _
                "1d Chg", "1w Chg", "1m Chg")

    ' anchor targets, same convention as the app: yesterday, -7 days, same day last month
    t1 = Date - 1
    t7 = Date - 7
    t30 = DateAdd("m", -1, Date)

    Application.DisplayAlerts = False
    On Error Resume Next
    ThisWorkbook.Worksheets("~RunsTmp").Delete
    On Error GoTo 0
    Set tmp = ThisWorkbook.Worksheets.Add
    tmp.Name = "~RunsTmp"
    tmp.Visible = xlSheetHidden

    tmp.Cells(1, 1).Value = "DRAX OIS Runs " & Format(Date, "dmmmyy")
    tmp.Cells(1, 1).Font.Bold = True
    r = 3
    For b = 0 To UBound(names)
        Set cur = Nothing
        On Error Resume Next
        Set cur = ThisWorkbook.Worksheets("Current").ListObjects(CStr(names(b)))
        On Error GoTo 0
        If Not cur Is Nothing Then
            If Not cur.DataBodyRange Is Nothing Then
                h = LoadHist(CStr(shts(b)))
                rowsOut = 0
                For i = 1 To cur.ListRows.Count
                    sd = SafeDate(cur.DataBodyRange(i, 3).Value)
                    If Not IsEmpty(sd) Then
                        If rowsOut = 0 Then
                            tmp.Cells(r, 1).Value = labels(b) & " closing run"
                            tmp.Cells(r, 1).Font.Bold = True
                            r = r + 1
                            For c = 0 To 7
                                tmp.Cells(r, c + 1).Value = hdr(c)
                                tmp.Cells(r, c + 1).Font.Bold = True
                                tmp.Cells(r, c + 1).Interior.Color = RGB(179, 227, 248)
                            Next c
                            r = r + 1
                        End If
                        md = SafeNum(cur.DataBodyRange(i, 5).Value)
                        tmp.Cells(r, 1).Value = sd
                        tmp.Cells(r, 1).NumberFormat = "dd-mmm-yy"
                        tmp.Cells(r, 2).Value = SafeDate(cur.DataBodyRange(i, 4).Value)
                        tmp.Cells(r, 2).NumberFormat = "dd-mmm-yy"
                        tmp.Cells(r, 3).Value = md
                        tmp.Cells(r, 3).NumberFormat = "0.000"
                        tmp.Cells(r, 4).Value = SafeNum(cur.DataBodyRange(i, 7).Value)
                        tmp.Cells(r, 5).Value = SafeNum(cur.DataBodyRange(i, 6).Value)
                        ' the three changes, off this workbook's own stored history
                        tmp.Cells(r, 6).Value = ChgBp(md, RateOn(h, sd, t1, t1 - TOL_1D))
                        tmp.Cells(r, 7).Value = ChgBp(md, RateOn(h, sd, t7, t7 - TOL_1W))
                        tmp.Cells(r, 8).Value = ChgBp(md, RateOn(h, sd, t30, t30 - TOL_1M))
                        For c = 6 To 8
                            tmp.Cells(r, c).NumberFormat = "+0.0;-0.0;0.0"
                        Next c
                        r = r + 1
                        rowsOut = rowsOut + 1
                    End If
                Next i
                If rowsOut > 0 Then
                    banks = banks + 1
                    r = r + 1
                End If
            End If
        End If
    Next b

    If banks = 0 Then
        tmp.Delete
        Application.DisplayAlerts = True
        If Not SuppressPrompts Then MsgBox "No bank produced any rows, so the Runs page has been LEFT AS IT WAS." & vbCrLf & vbCrLf & _
               "The Current sheet's start dates are still #N/A, which means Bloomberg has not " & _
               "returned yet. Wait for the sheet to finish calculating and press the button again.", _
               vbExclamation
        Exit Sub
    End If

    Set ws = ThisWorkbook.Worksheets("Runs")
    ws.Cells.Clear
    tmp.UsedRange.Copy
    ws.Cells(1, 1).PasteSpecial xlPasteAll
    Application.CutCopyMode = False
    ws.Columns("A:H").ColumnWidth = 11
    tmp.Delete
    Application.DisplayAlerts = True
    ws.Activate

    If SuppressPrompts Then Exit Sub
    MsgBox "All banks stored. Runs page rebuilt for " & Format(Date, "dd-mmm-yy") & _
           " from " & banks & " of 9 banks." & vbCrLf & _
           "Change columns computed from this workbook's Historical_ sheets.", vbInformation
End Sub
