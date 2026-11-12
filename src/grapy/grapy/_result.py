"""arcpy's Result: what a tool answers — its outputs, read by index or ``getOutput``, and, since ADR-289 §5.3, the
tool's own messages and inputs, which ``_runtime.run_tool`` hands it when the tool ends."""


class Result:
    def __init__(self, *outputs):
        self._outputs = list(outputs)
        self.status = 4
        self.outputCount = len(self._outputs)
        self._messages = []   # [{"kind", "code", "text"}], as _runtime keeps a tool's messages
        self._inputs = []

    def getOutput(self, index):
        return self._outputs[index]

    def __getitem__(self, index):
        return self._outputs[index]

    def __str__(self):
        return str(self._outputs[0]) if self._outputs else ""

    def __repr__(self):
        return f"<Result '{self}'>"

    # ---- the tool's messages, kept after other tools run (ArcGIS Pro 3.7, measured 2026-11-12)

    @property
    def messageCount(self):
        return len(self._messages)

    @property
    def maxSeverity(self):
        return max((self._severity(m) for m in self._messages), default=0)

    @staticmethod
    def _severity(message):
        return {50: 1, 100: 2}.get(message["kind"], 0)

    def getMessages(self, severity=0):
        """As arcpy.GetMessages, for this tool: all its messages one to a line, or with 1 its warnings and with 2 its
        errors, each followed by a newline."""
        level = int(severity)
        if level == 0:
            return "\n".join(m["text"] for m in self._messages)
        wanted = {1: 50, 2: 100}[level]
        return "".join(m["text"] + "\n" for m in self._messages if m["kind"] == wanted)

    def getMessage(self, index):
        """The text of the message at ``index``; "" past the last one, as Pro answers."""
        index = int(index)
        return self._messages[index]["text"] if -len(self._messages) <= index < len(self._messages) else ""

    def getSeverity(self, index):
        """0, 1 or 2 for the message at ``index``. INFERRED: 0 past the last one, where Pro answers an unset number."""
        index = int(index)
        if -len(self._messages) <= index < len(self._messages):
            return self._severity(self._messages[index])
        return 0

    def getAllMessages(self):
        return [[m["kind"], m["code"], m["text"]] for m in self._messages]

    # ---- the tool's inputs

    @property
    def inputCount(self):
        return len(self._inputs)

    def getInput(self, index):
        """An input's value as text. Past the last one Pro raises IndexError ("ResultObject: Error in getting input",
        measured 2026-11-12)."""
        try:
            return self._inputs[index]
        except IndexError:
            raise IndexError("ResultObject: Error in getting input") from None
