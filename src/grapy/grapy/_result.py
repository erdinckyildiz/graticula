"""arcpy's Result: what a tool answers — its outputs, read by index or ``getOutput``."""


class Result:
    def __init__(self, *outputs):
        self._outputs = list(outputs)
        self.status = 4
        self.outputCount = len(self._outputs)

    def getOutput(self, index):
        return self._outputs[index]

    def __getitem__(self, index):
        return self._outputs[index]

    def __str__(self):
        return str(self._outputs[0]) if self._outputs else ""

    def __repr__(self):
        return f"<Result '{self}'>"
