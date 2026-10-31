"""ADR-177 condition 2: QGIS's MetaSearch dialog, inside a running QGIS (offscreen, its own profile), pointed at this
server's OGC API Records catalogue: the connection saved in MetaSearch's settings, Search clicked, each record selected,
and for the first that offers it, Add WFS (then Add WMS) triggered from the dialog; in the provider window MetaSearch
opens, the first layer is selected and Add pressed. Writes what happened to OUT, then quits QGIS.

Run: QT_QPA_PLATFORM=offscreen qgis-ltr --profiles-path <scratch> --code <this file>.
"""
import json
import os
import traceback

from qgis.core import QgsProject, QgsSettings
from qgis.gui import QgsAbstractDataSourceWidget
from qgis.PyQt.QtCore import QTimer
from qgis.PyQt.QtWidgets import QAbstractItemView, QMessageBox, QTreeView, QWidget
from qgis.utils import iface

OUT = os.environ.get("MS_OUT", r"D:\temp\grapyc-qgis\metasearch.json")
URL = os.environ.get("MS_URL", "http://127.0.0.1:18575/ogc/records/v1/collections/catalog")
KEYWORDS = os.environ.get("MS_Q", "ci_parcels")
ACTIONS = os.environ.get("MS_ACTIONS", "mActionAddWfs,mActionAddWms").split(",")
said = {"url": URL, "steps": []}
state = {}


def finish():
    said["project_layers"] = [{"name": l.name(), "provider": l.providerType(), "valid": l.isValid(),
                               "features": l.featureCount() if hasattr(l, "featureCount") else None,
                               "source": l.source()[:200]}
                              for l in QgsProject.instance().mapLayers().values()]
    with open(OUT, "w", encoding="utf-8") as f:
        json.dump(said, f, indent=1, default=str)
    iface.actionExit().trigger()


def warn(parent, title, text, *args, **kwargs):
    said.setdefault("warnings", []).append(f"{title}: {text}")
    return QMessageBox.StandardButton.Yes


QMessageBox.warning = warn
QMessageBox.critical = warn
QMessageBox.information = warn


def search():
    from MetaSearch.dialogs import maindialog
    from MetaSearch.dialogs.maindialog import MetaSearchDialog

    # Hold the provider window MetaSearch opens, so this script can press its Add button.
    real = maindialog.QgsGui

    class Registry:
        def createSelectionWidget(self, *args, **kwargs):
            widget = real.sourceSelectProviderRegistry().createSelectionWidget(*args, **kwargs)
            state["made"] = widget
            return widget

    class Gui:
        @staticmethod
        def sourceSelectProviderRegistry():
            return Registry()

    maindialog.QgsGui = Gui
    s = QgsSettings()
    s.setValue("/MetaSearch/graticula/url", URL)
    s.setValue("/MetaSearch/graticula/catalog-type", "OGC API - Records")
    s.setValue("/MetaSearch/selected", "graticula")
    dlg = MetaSearchDialog(iface)
    dlg.show()
    dlg.populate_connection_list()
    dlg.tabWidget.setCurrentIndex(0)
    dlg.cmbConnectionsSearch.setCurrentIndex(dlg.cmbConnectionsSearch.findText("graticula"))
    dlg.leKeywords.setText(KEYWORDS)
    dlg.btnSearch.click()
    said["results"] = dlg.lblResults.text()
    state["dlg"] = dlg
    buttons = []
    for n in range(dlg.treeRecords.topLevelItemCount()):
        item = dlg.treeRecords.topLevelItem(n)
        dlg.treeRecords.setCurrentItem(item)
        buttons.append({"record": item.text(1), "type": item.text(0),
                        **{a: getattr(dlg, a).isEnabled() for a in
                           ("mActionAddWms", "mActionAddWfs", "mActionAddAms", "mActionAddAfs", "mActionAddWcs")}})
    said["buttons"] = buttons


def trigger(action):
    dlg = state["dlg"]
    for n in range(dlg.treeRecords.topLevelItemCount()):
        item = dlg.treeRecords.topLevelItem(n)
        dlg.treeRecords.setCurrentItem(item)
        if getattr(dlg, action).isEnabled():
            before = set(id(w) for w in dlg.findChildren(QWidget))
            getattr(dlg, action).trigger()
            widgets = [w for w in dlg.findChildren(QWidget) if id(w) not in before and isinstance(w, QgsAbstractDataSourceWidget)]
            if not widgets:
                widgets = [w for w in dlg.findChildren(QWidget) if id(w) not in before and type(w).__name__ == "QDialog"]
            said["steps"].append({"new_widget_types": sorted({type(w).__name__ for w in dlg.findChildren(QWidget) if id(w) not in before})[:15]})
            state["provider"] = state.pop("made", None)
            said["steps"].append({"action": action, "record": item.text(1), "type": item.text(0),
                                  "provider": type(state["provider"]).__name__ if state["provider"] else None})
            return True
    said["steps"].append({"action": action, "offered": False})
    return False


def add_first():
    w = state.get("provider")
    if w is None:
        return
    trees = [t for t in w.findChildren(QTreeView) if t.model() is not None and t.model().rowCount() > 0]
    said["steps"].append({"provider_rows": [t.model().rowCount() for t in trees]})
    if not trees:
        return
    tree = trees[0]
    tree.setSelectionMode(QAbstractItemView.SelectionMode.SingleSelection)
    tree.selectionModel().clearSelection()
    tree.setCurrentIndex(tree.model().index(0, 0))
    tree.selectionModel().select(tree.model().index(0, 0),
                                 tree.selectionModel().SelectionFlag.Select | tree.selectionModel().SelectionFlag.Rows)
    from qgis.PyQt.QtCore import QMetaObject
    said["steps"].append({"add_invoked": QMetaObject.invokeMethod(w, "addButtonClicked"),
                          "class": w.metaObject().className()})


plan = []
for action in ACTIONS:
    plan += [lambda a=action: trigger(a), add_first]


def step(i=0):
    try:
        if i == 0:
            search()
        elif i - 1 < len(plan):
            plan[i - 1]()
        else:
            QTimer.singleShot(4000, finish)
            return
    except Exception:  # noqa: BLE001
        said.setdefault("errors", []).append(traceback.format_exc())
    QTimer.singleShot(5000, lambda: step(i + 1))


QTimer.singleShot(3000, step)
