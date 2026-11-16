"""Writes the XLSForm workbooks the form tests read (ADR-287 condition 7).

They are written by openpyxl, not by FormWorkbook, so the reader is tested against a workbook it did not write.
Run with: python -I make-fixtures.py  (from this folder; needs openpyxl 3.1). The .xlsx files are committed, so
the tests do not need Python; this script is how they were made and how to make them again.
"""

import sys
from pathlib import Path

from openpyxl import Workbook
from openpyxl.cell.rich_text import CellRichText, TextBlock
from openpyxl.cell.text import InlineFont

here = Path(sys.argv[0]).resolve().parent


def inspection():
    book = Workbook()
    survey = book.active
    survey.title = "survey"
    rows = [
        ["type", "name", "label::Türkçe (tr)", "label::English (en)", "required", "relevant", "constraint",
         "calculation", "appearance", "bind::esri:fieldLength", "kendi_sütunumuz"],
        ["start", "start"],
        ["geopoint", "konum", "Konum", "Location", "yes"],
        ["text", "bina_adi", "Bina adı", "Building name", "yes", None, None, None, None, 120],
        ["integer", "kat_sayisi", "Kat sayısı", "Floors", "yes", None, ". > 0 and . < 200"],
        ["select_one hasar", "hasar", "Hasar", "Damage", "true()"],
        ["image", "foto", "Fotoğraf", "Photo", None, "${hasar} != 'yok'"],
        [],
        ["begin repeat", "katlar", "Katlar", "Floors"],
        ["integer", "kat_no", "Kat no", "Floor number"],
        ["begin repeat", "odalar", "Odalar", "Rooms"],
        ["text", "oda", "Oda", "Room", None, None, None, None, None, None, "korunmalı"],
        ["end repeat"],
        ["end repeat"],
        ["calculate", "toplam", None, None, None, None, None, "count(${katlar})"],
        ["decimal", "oran", "  baştaki boşluk korunur", "leading space kept"],
    ]
    for row in rows:
        survey.append(row)
    # A cell past the header, in a column with no name: kept, because the model keeps everything.
    survey.cell(row=4, column=14, value="başlıksız")
    # A rich-text label: runs that the reader must join.
    survey.cell(row=5, column=3).value = CellRichText(
        "Kat ", TextBlock(InlineFont(b=True), "sayısı"))
    # A boolean and a float as typed cells.
    survey.cell(row=6, column=5).value = True
    survey.cell(row=16, column=10).value = 0.5

    choices = book.create_sheet("choices")
    for row in [
        ["list_name", "name", "label::Türkçe (tr)", "label::English (en)"],
        ["hasar", "yok", "Yok", "None"],
        ["hasar", "az", "Az", "Light"],
        ["hasar", "agir", "Ağır", "Heavy"],
        ["hasar", 1, "Bir", "One"],
    ]:
        choices.append(row)

    settings = book.create_sheet("settings")
    for row in [
        ["form_title", "form_id", "version", "default_language", "instance_name"],
        ["Bina denetimi", "bina_denetimi", 2026110901, "Türkçe (tr)", "concat(${bina_adi}, ' ', ${start})"],
    ]:
        settings.append(row)

    book.create_sheet("notlar").append(["Bu sayfa formun parçası değil ama korunur."])
    book.save(here / "inspection.xlsx")


if __name__ == "__main__":
    inspection()
