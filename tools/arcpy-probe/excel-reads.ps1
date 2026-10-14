# ADR-106 condition 3: Excel opens this server's CSV and workbook exports, and the header row and the first rows are
# read back as Excel shows them. Excel itself is driven over COM, so this is Excel's own reading, not a library's.
#   powershell -NoProfile -File excel-reads.ps1 <file.csv> <file.xlsx>   (measured with Microsoft 365 Excel, 2026-10-14)
param([Parameter(Mandatory)][string[]]$Files)
$excel = New-Object -ComObject Excel.Application
$excel.Visible = $false
$excel.DisplayAlerts = $false
try {
    foreach ($file in $Files) {
        $book = $excel.Workbooks.Open((Resolve-Path $file).Path, 0, $true)
        $sheet = $book.Worksheets.Item(1)
        $used = $sheet.UsedRange
        "== $file  ($($used.Rows.Count) rows, $($used.Columns.Count) columns)"
        for ($r = 1; $r -le [Math]::Min(4, $used.Rows.Count); $r++) {
            $cells = for ($c = 1; $c -le $used.Columns.Count; $c++) { [string]$sheet.Cells.Item($r, $c).Text }
            "   " + ($cells -join ' | ')
        }
        $book.Close($false)
    }
} finally {
    $excel.Quit()
    [void][Runtime.InteropServices.Marshal]::ReleaseComObject($excel)
}
