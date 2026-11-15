/*
  The XLSForm expression evaluator — ADR-287 §5.4, §5.9 (a) and (i), §5.10, conditions 3 and 4.

  <b>Written here, from the public specifications only.</b> The owner chose to build the form client rather than adopt
  Enketo or ODK Web Forms (§5.9 (a)), and nothing below is taken from either, or from any other implementation. The
  sources are the ODK XForms specification (https://getodk.github.io/xforms-spec/), XPath 1.0 (which it extends) and
  xlsform.org. Where they leave a choice open, the choice is said at the place it is made, marked INFERRED.

  <b>One global, because classic scripts share one namespace</b>: a second top-level function of the same name in
  another script would silently replace the first. Everything is inside this function and the only name it leaves
  behind is window.GraticulaXlsform.

  <b>The function list is FormCatalog.Functions</b> (src/Graticula.Host/Forms/FormCatalog.cs), the one place §5.10
  keeps it. XlsformEvaluatorTests reads that file and fails if the two lists differ, in either direction.

  <b>The evaluation context</b> — what the caller supplies, all optional except value():

    value(name, path)  the answer(s) of ${name}. A string (or number) for one answer; an array for several — the
                       instances of a repeat for count(${repeat}), or every instance's answer of a question inside a
                       repeat when it is referred to from outside it, for sum(${q}). null or undefined: no such node.
                       path is given only by indexed-repeat(): [{ repeat: 'name', index: 1 }, …], outermost first, and
                       the answer is then the one in that instance.
    current            the value of '.', the question the expression belongs to.
    position           the 1-based index of the repeat instance the expression is evaluated in, for position(..).
    choices(listName)  the rows of a choice list, as objects keyed by the choices sheet's columns (name, label,
                       label::English, any filter column). Used by instance('list')/root/item[…]/name.
    listOf(name)       the choice list a select question uses, for jr:choice-name.
    language           the active language, for jr:choice-name's label::language column.
    pulldata(file, column, keyColumn, key)  a value from a CSV uploaded with the form, or '' when none.
    now()              a Date standing for the present moment; new Date() when absent. For tests.
    timezoneOffset     the device's offset from UTC in minutes (Turkey: 180); the browser's when absent.
    random()           a number in [0, 1); Math.random when absent. For tests.

  <b>Multi-select answers are comma-separated in this product</b> (§5.10: Survey123 stores them so, and the layer keeps
  that), while XForms separates them with spaces. selected(), selected-at() and count-selected() accept either, and a
  mixture: a choice name is an XML name in XLSForm, so it can hold neither a space nor a comma, and splitting on both
  cannot cut a name in two.
*/
(function () {
    'use strict';

    // ---------------------------------------------------------------------------------------------------------------
    // The function table: name → [fewest arguments, most arguments]. FormCatalog.Functions, exactly.
    // ---------------------------------------------------------------------------------------------------------------

    var MANY = Infinity;

    var ARITY = {
        // String
        'string': [0, 1], 'concat': [0, MANY], 'join': [1, MANY], 'substr': [2, 3], 'substring-before': [2, 2],
        'substring-after': [2, 2], 'translate': [3, 3], 'string-length': [1, 1], 'normalize-space': [0, 1],
        'contains': [2, 2], 'starts-with': [2, 2], 'ends-with': [2, 2], 'uuid': [0, 1], 'pulldata': [2, 4], 'property': [1, 1],
        'substring': [2, 3],
        // Boolean
        'if': [3, 3], 'coalesce': [2, 2], 'once': [1, 1], 'true': [0, 0], 'false': [0, 0], 'boolean': [1, 1],
        'boolean-from-string': [1, 1], 'not': [1, 1], 'regex': [2, 2], 'checklist': [2, MANY],
        'weighted-checklist': [2, MANY],
        // Number
        'number': [0, 1], 'random': [0, 0], 'int': [1, 1], 'sum': [1, 1], 'max': [1, MANY], 'min': [1, MANY],
        'round': [1, 2], 'pow': [2, 2], 'log': [1, 1], 'log10': [1, 1], 'abs': [1, 1], 'sin': [1, 1], 'cos': [1, 1],
        'tan': [1, 1], 'asin': [1, 1], 'acos': [1, 1], 'atan': [1, 1], 'atan2': [2, 2], 'sqrt': [1, 1],
        'exp': [1, 1], 'exp10': [1, 1], 'pi': [0, 0], 'floor': [1, 1], 'ceiling': [1, 1],
        // Node-set
        'count': [1, 1], 'count-non-empty': [1, 1], 'position': [0, 1], 'current': [0, 0], 'randomize': [1, 2],
        'instance': [1, 1], 'last': [0, 0],
        // Date and time
        'today': [0, 0], 'now': [0, 0], 'format-date': [2, 2], 'format-date-time': [2, 2], 'date': [1, 1],
        'decimal-date-time': [1, 1], 'decimal-time': [1, 1], 'date-time': [1, 1],
        // Select
        'selected': [2, 2], 'selected-at': [2, 2], 'count-selected': [1, 1], 'jr:choice-name': [2, 2],
        // Repeat
        'indexed-repeat': [3, MANY],
        // Geographic
        'area': [1, 1], 'distance': [1, MANY], 'geofence': [2, 2], 'intersects': [1, 1]
    };

    // Refused by FormCatalog.RefusedFunctions; named so the message is not "unknown".
    var REFUSED = {
        'digest': 'digests are not in the first version',
        'base64-decode': 'base64 decoding is not in the first version',
        'jr:itext': 'jr:itext is an internal XForms function; use a label::language column',
        'lang': 'lang() reads the xml:lang of a node, and a form\'s answers carry none; the language is the form\'s default_language',
        'id': 'id() looks elements up by an ID attribute, which an XForm does not use; a question is named by ${name}',
        'local-name': 'node names are not in the first version; a question is named by ${name}',
        'namespace-uri': 'namespaces are not in the first version; a question is named by ${name}',
        'name': 'node names are not in the first version; a question is named by ${name}'
    };

    // ADR-287 §5.20 — pulldata("@property"), property() and pulldata("@geopoint"). These lists are FormCatalog's
    // (Properties, RefusedProperties, OdkProperties, RefusedOdkProperties, GeopointParts, GeopointPartsAnsweredEmpty,
    // RefusedGeopointParts, RefusedPulldataSources, CalculationModes); XlsformEvaluatorTests holds each pair equal.
    var PROPERTIES = ['online', 'utcOffset', 'timeZone', 'deviceId', 'userAgent', 'email', 'username', 'userFullName',
        'userFirstName', 'userLastName', 'portalUrl', 'language', 'surveyName', 'mode', 'status'];
    var REFUSED_PROPERTIES = {
        'deviceModel': 'a browser does not tell a page the model of the device',
        'OS': 'a browser does not tell a page the operating system\'s name',
        'appVersion': 'this client has no build number to give',
        'portalInfo': 'the whole portal description is a JSON object, which no question in a form can hold',
        'token': 'an access token is a credential, and a form\'s answers are stored and shown',
        'submissionUrl': 'it exists to point pulldata("@layer") and search() at the layer, and neither is in the first version',
        'locale': 'Survey123 offers it only to pulldata("@javascript"), which is refused',
        'localeInfo': 'Survey123 offers it only to pulldata("@javascript"), which is refused'
    };
    var ODK_PROPERTIES = { 'deviceid': 'deviceId', 'username': 'username', 'email': 'email' };
    var REFUSED_ODK_PROPERTIES = {
        'subscriberid': 'a browser cannot read the SIM\'s subscriber id',
        'simserial': 'a browser cannot read the SIM\'s serial number',
        'phonenumber': 'a browser cannot read the device\'s phone number'
    };
    var GEOPOINT_PARTS = ['x', 'y', 'z', 'horizontalAccuracy', 'accuracy', 'positionSourceType', 'DMS',
        'DMS.latitudeDegrees', 'DMS.latitudeMinutes', 'DMS.latitudeSeconds', 'DMS.latitudeHemisphere', 'DMS.latitudeText',
        'DMS.longitudeDegrees', 'DMS.longitudeMinutes', 'DMS.longitudeSeconds', 'DMS.longitudeHemisphere', 'DMS.longitudeText',
        'DMS.text'];
    var GEOPOINT_PARTS_EMPTY = ['verticalAccuracy', 'speed', 'verticalSpeed', 'direction', 'magneticVariation', 'fixType',
        'positionSourceInfo', 'positionSourceInfo.antennaHeight', 'positionSourceInfo.altitudeType',
        'positionSourceInfo.pluginName', 'positionSourceInfo.deviceAddress', 'positionSourceInfo.deviceName',
        'positionSourceInfo.deviceType', 'positionSourceInfo.networkName', 'positionSourceInfo.networkAddress',
        'positionSourceInfo.networkPort', 'positionSourceInfo.geoidSeparationCustom', 'geoidSeparation', 'accuracyType',
        'positionAccuracy', 'latitudeError', 'longitudeError', 'altitudeError', 'hdop', 'vdop', 'pdop', 'differentialAge',
        'referenceStationId', 'satellitesVisible', 'satellitesInUse'];
    var REFUSED_GEOPOINT_PARTS = {
        'UTM': 'converting to UTM is not built', 'UTM.easting': 'converting to UTM is not built',
        'UTM.northing': 'converting to UTM is not built', 'UTM.zone': 'converting to UTM is not built',
        'UTM.hemisphere': 'converting to UTM is not built', 'MGRS': 'converting to MGRS is not built'
    };
    var REFUSED_PULLDATA_SOURCES = {
        '@layer': 'it queries a feature layer, and an offline client would need a copy of that layer on the device',
        '@json': 'reading a JSON answer is not in the first version',
        '@javascript': 'running a script a form carries is not in the first version',
        '@exif': 'reading the metadata of a photo is not in the first version'
    };
    var CALCULATION_MODES = ['auto', 'manual', 'always', 'whenEmpty'];

    function listHas(list, item) { return list.indexOf(item) >= 0; }
    function hasKey(object, key) { return Object.prototype.hasOwnProperty.call(object, key); }

    // The XPath 1.0 axes: the only words that may stand before '::' (XPath 1.0 §2.2).
    var AXES = ['ancestor', 'ancestor-or-self', 'attribute', 'child', 'descendant', 'descendant-or-self', 'following',
        'following-sibling', 'namespace', 'parent', 'preceding', 'preceding-sibling', 'self'];

    // XPath node-type tests, which look like calls but are steps.
    var NODE_TESTS = { 'node': true, 'text': true };

    // ---------------------------------------------------------------------------------------------------------------
    // Errors carry the character they are about: message 1-based, as FormExpressions.cs words it; .index 0-based.
    // ---------------------------------------------------------------------------------------------------------------

    function fail(message, index) {
        var error = new Error(message + (index === undefined ? '' : ' at character ' + (index + 1)));
        error.index = index;
        throw error;
    }

    // ---------------------------------------------------------------------------------------------------------------
    // Lexer.
    // ---------------------------------------------------------------------------------------------------------------

    function isNameStart(c) {
        return /[A-Za-z_À-￿]/.test(c);
    }

    function isNameChar(c) {
        return /[A-Za-z0-9_.\-·À-￿]/.test(c);
    }

    // XPath 1.0 §3.7: after one of these, '*' is a name test and and/or/div/mod are names, not operators.
    function endsOperand(token) {
        if (!token) { return false; }
        if (token.t === 'num' || token.t === 'str' || token.t === 'ref' || token.t === 'name') { return true; }
        return token.t === 'p' && (token.v === ')' || token.v === ']' || token.v === '.' || token.v === '..' || token.v === '*');
    }

    function tokenize(text) {
        var tokens = [];
        var i = 0;
        var n = text.length;

        while (i < n) {
            var c = text.charAt(i);
            var start = i;
            var last = tokens.length ? tokens[tokens.length - 1] : null;

            if (/\s/.test(c)) { i++; continue; }

            if (c === '\'' || c === '"') {
                var close = text.indexOf(c, i + 1);
                if (close < 0) { fail('A string is never closed', start); }
                tokens.push({ t: 'str', v: text.substring(i + 1, close), at: start });
                i = close + 1;
                continue;
            }

            if (c === '$' && text.charAt(i + 1) === '{') {
                var end = text.indexOf('}', i + 2);
                if (end < 0) { fail('A reference has no closing \'}\'', start); }
                var ref = text.substring(i + 2, end).trim();
                if (!/^[A-Za-z_À-￿][A-Za-z0-9_.\-À-￿]*$/.test(ref)) {
                    fail('\'${' + ref + '}\' is not a question name', start);
                }
                tokens.push({ t: 'ref', v: ref, at: start });
                i = end + 1;
                continue;
            }

            if (/[0-9]/.test(c) || (c === '.' && /[0-9]/.test(text.charAt(i + 1)))) {
                while (i < n && /[0-9]/.test(text.charAt(i))) { i++; }
                if (text.charAt(i) === '.') {
                    i++;
                    while (i < n && /[0-9]/.test(text.charAt(i))) { i++; }
                }
                tokens.push({ t: 'num', v: parseFloat(text.substring(start, i)), at: start });
                continue;
            }

            if (c === '.' && text.charAt(i + 1) === '.') { tokens.push({ t: 'p', v: '..', at: start }); i += 2; continue; }

            var two = text.substr(i, 2);
            if (two === '!=' || two === '<=' || two === '>=' || two === '//' || two === '::') {
                tokens.push({ t: 'p', v: two, at: start });
                i += 2;
                continue;
            }

            if ('()[],/|+-=<>.@'.indexOf(c) >= 0) { tokens.push({ t: 'p', v: c, at: start }); i++; continue; }

            if (c === '*') {
                tokens.push(endsOperand(last) ? { t: 'op', v: '*', at: start } : { t: 'p', v: '*', at: start });
                i++;
                continue;
            }

            if (isNameStart(c)) {
                while (i < n && isNameChar(text.charAt(i))) { i++; }
                // A prefixed name such as jr:choice-name — one colon, a name on each side, not the axis '::'.
                if (text.charAt(i) === ':' && text.charAt(i + 1) !== ':' && isNameStart(text.charAt(i + 1))) {
                    i++;
                    while (i < n && isNameChar(text.charAt(i))) { i++; }
                }
                // A choices column named with a language, label::English, is one name: a choice_filter reads it as one.
                // Only an XPath axis may stand before '::', so child::x and parent::x keep their meaning.
                if (text.substr(i, 2) === '::' && isNameStart(text.charAt(i + 2)) && AXES.indexOf(text.substring(start, i)) < 0) {
                    i += 2;
                    while (i < n && isNameChar(text.charAt(i))) { i++; }
                }
                var word = text.substring(start, i);
                // As in XPath, '-' and '.' are name characters: a-1 is one name, and a - 1 is a subtraction.
                if (endsOperand(last) && (word === 'and' || word === 'or' || word === 'div' || word === 'mod')) {
                    tokens.push({ t: 'op', v: word, at: start });
                } else {
                    tokens.push({ t: 'name', v: word, at: start });
                }
                continue;
            }

            fail('Unexpected \'' + c + '\'', start);
        }

        tokens.push({ t: 'end', v: '', at: n });
        return tokens;
    }

    // ---------------------------------------------------------------------------------------------------------------
    // Parser: XPath 1.0's grammar (§3), lowest precedence first —
    //   or < and < = != < < <= > >= < + - < * div mod < unary - < | < path.
    // ---------------------------------------------------------------------------------------------------------------

    function parse(expression) {
        if (typeof expression !== 'string') { fail('An expression must be text'); }
        var tokens = tokenize(expression);
        var k = 0;

        function peek() { return tokens[k]; }
        function next() { return tokens[k++]; }
        function isP(v) { var t = tokens[k]; return t.t === 'p' && t.v === v; }
        function isOp(v) { var t = tokens[k]; return t.t === 'op' && t.v === v; }
        function expect(v) {
            var t = tokens[k];
            if (t.t === 'p' && t.v === v) { k++; return t; }
            return fail('Expected \'' + v + '\' but found ' + describe(t), t.at);
        }
        function describe(t) { return t.t === 'end' ? 'the end of the expression' : '\'' + (t.t === 'str' ? t.v : String(t.v)) + '\''; }

        function binary(sub, matches) {
            return function () {
                var left = sub();
                for (;;) {
                    var t = peek();
                    var op = matches(t);
                    if (!op) { return left; }
                    k++;
                    left = { type: 'bin', op: op, left: left, right: sub(), at: t.at };
                }
            };
        }

        var unionExpr;
        var unaryExpr = function () {
            if (isP('-')) {
                var t = next();
                return { type: 'neg', arg: unaryExpr(), at: t.at };
            }
            return unionExpr();
        };
        var multiplicative = binary(unaryExpr, function (t) {
            return t.t === 'op' && (t.v === '*' || t.v === 'div' || t.v === 'mod') ? t.v : null;
        });
        var additive = binary(multiplicative, function (t) {
            return t.t === 'p' && (t.v === '+' || t.v === '-') ? t.v : null;
        });
        var relational = binary(additive, function (t) {
            return t.t === 'p' && (t.v === '<' || t.v === '<=' || t.v === '>' || t.v === '>=') ? t.v : null;
        });
        var equality = binary(relational, function (t) {
            return t.t === 'p' && (t.v === '=' || t.v === '!=') ? t.v : null;
        });
        var andExpr = binary(equality, function (t) { return t.t === 'op' && t.v === 'and' ? 'and' : null; });
        var orExpr = binary(andExpr, function (t) { return t.t === 'op' && t.v === 'or' ? 'or' : null; });

        unionExpr = function () {
            var left = pathExpr();
            while (isP('|')) {
                var t = next();
                left = { type: 'bin', op: '|', left: left, right: pathExpr(), at: t.at };
            }
            return left;
        };

        function predicates() {
            var list = [];
            while (isP('[')) {
                next();
                list.push(orExpr());
                expect(']');
            }
            return list;
        }

        function startsPrimary() {
            var t = peek();
            if (t.t === 'num' || t.t === 'str' || t.t === 'ref') { return true; }
            if (t.t === 'p' && t.v === '(') { return true; }
            return t.t === 'name' && tokens[k + 1].t === 'p' && tokens[k + 1].v === '(' && !NODE_TESTS[t.v];
        }

        function pathExpr() {
            var t = peek();
            if (startsPrimary()) {
                var primary = primaryExpr();
                var preds = predicates();
                var base = preds.length ? { type: 'filter', base: primary, predicates: preds, at: primary.at } : primary;
                if (isP('/') || isP('//')) {
                    return { type: 'path', base: base, absolute: false, steps: relativeSteps(true), at: t.at };
                }
                return base;
            }
            if (isP('/')) {
                next();
                var steps = startsStep() ? relativeSteps(false) : [];
                return { type: 'path', base: null, absolute: true, steps: steps, at: t.at };
            }
            if (isP('//')) {
                next();
                var rest = relativeSteps(false);
                rest[0].descendant = true;
                return { type: 'path', base: null, absolute: true, steps: rest, at: t.at };
            }
            if (startsStep()) {
                return { type: 'path', base: null, absolute: false, steps: relativeSteps(false), at: t.at };
            }
            return fail('Expected a value but found ' + describe(t), t.at);
        }

        function startsStep() {
            var t = peek();
            return t.t === 'name' || (t.t === 'p' && (t.v === '.' || t.v === '..' || t.v === '*' || t.v === '@'));
        }

        // Steps after a separator; leading=true means a '/' or '//' comes first.
        function relativeSteps(leading) {
            var steps = [];
            var descendant = false;
            if (leading) { descendant = next().v === '//'; }
            for (;;) {
                var s = step();
                s.descendant = descendant;
                steps.push(s);
                if (isP('/') || isP('//')) { descendant = next().v === '//'; } else { return steps; }
            }
        }

        function step() {
            var t = next();
            if (t.t === 'p' && t.v === '.') { return { axis: 'self', name: '*', predicates: [], at: t.at }; }
            if (t.t === 'p' && t.v === '..') { return { axis: 'parent', name: '*', predicates: [], at: t.at }; }
            if (t.t === 'p' && t.v === '@') { return fail('Attributes are not supported', t.at); }
            var axis = 'child';
            if (t.t === 'name' && isP('::')) {
                if (t.v !== 'child' && t.v !== 'self' && t.v !== 'parent') {
                    fail('The ' + t.v + ' axis is not supported; use child, self or parent', t.at);
                }
                axis = t.v;
                next();
                t = next();
            }
            var name;
            if (t.t === 'p' && t.v === '*') {
                name = '*';
            } else if (t.t === 'name') {
                name = t.v;
                if (NODE_TESTS[name] && isP('(')) { next(); expect(')'); name = name === 'node' ? '*' : '#text'; }
            } else {
                return fail('Expected a name but found ' + describe(t), t.at);
            }
            return { axis: axis, name: name, predicates: predicates(), at: t.at };
        }

        function primaryExpr() {
            var t = next();
            if (t.t === 'num') { return { type: 'num', value: t.v, at: t.at }; }
            if (t.t === 'str') { return { type: 'str', value: t.v, at: t.at }; }
            if (t.t === 'ref') { return { type: 'ref', name: t.v, at: t.at }; }
            if (t.t === 'p' && t.v === '(') {
                var inner = orExpr();
                expect(')');
                return inner;
            }
            // A function call (startsPrimary checked the '(').
            var name = t.v;
            if (REFUSED[name]) { fail(name + '() is not supported: ' + REFUSED[name], t.at); }
            if (!ARITY[name]) { fail('Unknown function \'' + name + '\'', t.at); }
            expect('(');
            var args = [];
            if (!isP(')')) {
                args.push(orExpr());
                while (isP(',')) { next(); args.push(orExpr()); }
            }
            expect(')');
            var arity = ARITY[name];
            if (args.length < arity[0] || args.length > arity[1]) {
                fail(name + '() takes ' + (arity[0] === arity[1] ? arity[0]
                    : arity[1] === MANY ? 'at least ' + arity[0] : arity[0] + ' to ' + arity[1])
                    + ' argument' + (arity[1] === 1 && arity[0] === 1 ? '' : 's') + ', not ' + args.length, t.at);
            }
            if (name === 'weighted-checklist' && args.length % 2 !== 0) {
                fail('weighted-checklist() takes min, max and pairs of value and weight', t.at);
            }
            if (name === 'indexed-repeat' && args.length % 2 === 0) {
                fail('indexed-repeat() takes a question and pairs of repeat and index', t.at);
            }
            if (name === 'pulldata') {
                var source = args[0].type === 'str' ? args[0].value : '';
                if (source === '@property') {
                    if (args.length !== 2) { fail('pulldata("@property") takes the name of one property', t.at); }
                    if (args[1].type === 'str') {
                        var property = args[1].value;
                        if (hasKey(REFUSED_PROPERTIES, property)) { fail('pulldata("@property") does not give \'' + property + '\': ' + REFUSED_PROPERTIES[property], t.at); }
                        if (!listHas(PROPERTIES, property)) { fail('\'' + property + '\' is not a property Survey123 documents', t.at); }
                    }
                } else if (source === '@geopoint') {
                    if (args.length !== 3) { fail('pulldata("@geopoint") takes a geopoint question and the name of a part', t.at); }
                    if (args[2].type === 'str') {
                        var part = args[2].value;
                        if (hasKey(REFUSED_GEOPOINT_PARTS, part)) { fail('pulldata("@geopoint") does not give \'' + part + '\': ' + REFUSED_GEOPOINT_PARTS[part], t.at); }
                        if (!listHas(GEOPOINT_PARTS, part) && !listHas(GEOPOINT_PARTS_EMPTY, part)) { fail('\'' + part + '\' is not a part of a geopoint Survey123 documents', t.at); }
                    }
                } else if (source.charAt(0) === '@') {
                    fail('pulldata("' + source + '") is not supported: ' + (hasKey(REFUSED_PULLDATA_SOURCES, source)
                        ? REFUSED_PULLDATA_SOURCES[source] : 'it is not a source Survey123 documents'), t.at);
                } else if (args.length !== 4) {
                    fail('pulldata() takes 4 arguments, not ' + args.length, t.at);
                }
            }
            if (name === 'property' && args[0].type === 'str') {
                var written = args[0].value;
                var odk = written.indexOf('uri:') === 0 ? written.substring(4) : written;
                if (hasKey(REFUSED_ODK_PROPERTIES, odk)) { fail('property(\'' + written + '\') is not supported: ' + REFUSED_ODK_PROPERTIES[odk], t.at); }
                if (hasKey(REFUSED_PROPERTIES, odk)) { fail('property(\'' + written + '\') is not supported: ' + REFUSED_PROPERTIES[odk], t.at); }
                if (!hasKey(ODK_PROPERTIES, odk) && !listHas(PROPERTIES, odk)) { fail('\'' + written + '\' is not a property ODK or Survey123 documents', t.at); }
            }
            return { type: 'call', name: name, args: args, at: t.at };
        }

        var ast = orExpr();
        if (peek().t !== 'end') { fail('Unexpected ' + describe(peek()), peek().at); }
        return ast;
    }

    // ---------------------------------------------------------------------------------------------------------------
    // Values. A node-set is { nodes: [...] }; a date is { date: true, kind: 'date'|'dateTime', ... }; the rest are
    // JavaScript strings, numbers and booleans.
    // ---------------------------------------------------------------------------------------------------------------

    var DAY = 86400000;

    function isNodeSet(v) { return v !== null && typeof v === 'object' && Array.isArray(v.nodes); }
    function isDate(v) { return v !== null && typeof v === 'object' && v.date === true; }
    function nodeSet(nodes) { return { nodes: nodes }; }

    function pad(n, width) {
        var s = String(Math.abs(n));
        while (s.length < width) { s = '0' + s; }
        return (n < 0 ? '-' : '') + s;
    }

    // XPath 1.0 §4.2: integers without a point, no exponent, NaN and Infinity spelled out.
    function numberToString(n) {
        if (isNaN(n)) { return 'NaN'; }
        if (n === Infinity) { return 'Infinity'; }
        if (n === -Infinity) { return '-Infinity'; }
        if (n === 0) { return '0'; }
        var s = String(n);
        if (s.indexOf('e') < 0) { return s; }
        var abs = Math.abs(n);
        var text = abs >= 1 ? BigInt(Math.round(abs)).toString() : abs.toFixed(20).replace(/0+$/, '');
        return (n < 0 ? '-' : '') + text;
    }

    function stringValue(v) {
        if (typeof v === 'string') { return v; }
        if (typeof v === 'number') { return numberToString(v); }
        if (typeof v === 'boolean') { return v ? 'true' : 'false'; }
        if (isNodeSet(v)) { return v.nodes.length ? nodeValue(v.nodes[0]) : ''; }
        if (isDate(v)) { return dateToString(v); }
        return v === null || v === undefined ? '' : String(v);
    }

    function nodeValue(node) { return node.value === null || node.value === undefined ? '' : String(node.value); }

    // XPath 1.0's Number production, and — the ODK extension on number() — a date or dateTime string becomes days
    // since 1970-01-01. The extension is applied wherever a string becomes a number, not only in number(): that is
    // what lets ${end} - ${start} and ${d} < today() work, as the specification says dates do in arithmetic.
    function numberValue(v) {
        if (typeof v === 'number') { return v; }
        if (typeof v === 'boolean') { return v ? 1 : 0; }
        if (isDate(v)) { return dateToDays(v); }
        var s = stringValue(v).trim();
        if (/^-?(\d+(\.\d*)?|\.\d+)$/.test(s)) { return parseFloat(s); }
        var d = parseDateLike(s);
        return d ? dateToDays(d) : NaN;
    }

    // INFERRED: a node-set holding exactly one node is true when that node's value is non-empty, not merely because
    // the node exists (strict XPath 1.0). A relevant of ${q} is written by a form author to mean "q is answered", and
    // every question node exists in an XForms instance, so the strict rule would make it always true.
    function booleanValue(v) {
        if (typeof v === 'boolean') { return v; }
        if (typeof v === 'number') { return v !== 0 && !isNaN(v); }
        if (typeof v === 'string') { return v.length > 0; }
        if (isNodeSet(v)) { return v.nodes.length > 1 || (v.nodes.length === 1 && nodeValue(v.nodes[0]).length > 0); }
        if (isDate(v)) { return true; }
        return false;
    }

    // ---------------------------------------------------------------------------------------------------------------
    // Dates. A date is a calendar day (y, m, d), and its number is the days from 1970-01-01 to it. A dateTime is an
    // instant (ms) and the offset it was written with; its number is decimal days since 1970-01-01T00:00Z.
    // ---------------------------------------------------------------------------------------------------------------

    var DATE_RE = /^(\d{4})-(\d{2})-(\d{2})$/;
    var DATETIME_RE = /^(\d{4})-(\d{2})-(\d{2})T(\d{2}):(\d{2})(?::(\d{2})(?:\.(\d+))?)?(Z|[+-]\d{2}(?::?\d{2})?)?$/;
    var TIME_RE = /^(\d{2}):(\d{2})(?::(\d{2})(?:\.(\d+))?)?(Z|[+-]\d{2}(?::?\d{2})?)?$/;

    var envOffset = 0; // the device offset of the evaluation in progress, in minutes

    function makeDate(y, m, d) {
        var check = new Date(Date.UTC(y, m - 1, d));
        if (check.getUTCFullYear() !== y || check.getUTCMonth() !== m - 1 || check.getUTCDate() !== d) { return null; }
        return { date: true, kind: 'date', y: y, m: m, d: d };
    }

    function offsetMinutes(zone) {
        if (!zone) { return null; }
        if (zone === 'Z') { return 0; }
        var sign = zone.charAt(0) === '-' ? -1 : 1;
        var digits = zone.substring(1).replace(':', '');
        return sign * (parseInt(digits.substring(0, 2), 10) * 60 + (digits.length > 2 ? parseInt(digits.substring(2), 10) : 0));
    }

    function millis(fraction) { return fraction ? Math.round(parseFloat('0.' + fraction) * 1000) : 0; }

    function parseDateLike(s) {
        var m = DATE_RE.exec(s);
        if (m) { return makeDate(+m[1], +m[2], +m[3]); }
        m = DATETIME_RE.exec(s);
        if (m) {
            if (!makeDate(+m[1], +m[2], +m[3]) || +m[4] > 23 || +m[5] > 59 || (m[6] && +m[6] > 59)) { return null; }
            // A dateTime written without an offset is the device's local time.
            var offset = m[8] ? offsetMinutes(m[8]) : envOffset;
            var wall = Date.UTC(+m[1], +m[2] - 1, +m[3], +m[4], +m[5], m[6] ? +m[6] : 0, millis(m[7]));
            return { date: true, kind: 'dateTime', ms: wall - offset * 60000, offset: offset };
        }
        return null;
    }

    function dateToDays(v) {
        return v.kind === 'date' ? Date.UTC(v.y, v.m - 1, v.d) / DAY : v.ms / DAY;
    }

    // The wall-clock fields of a value in the device's offset, as format-date-time and decimal-time read it.
    function wallFields(v) {
        if (v.kind === 'date') { return { y: v.y, m: v.m, d: v.d, H: 0, M: 0, S: 0, ms: 0, wd: new Date(Date.UTC(v.y, v.m - 1, v.d)).getUTCDay() }; }
        var t = new Date(v.ms + envOffset * 60000);
        return {
            y: t.getUTCFullYear(), m: t.getUTCMonth() + 1, d: t.getUTCDate(), H: t.getUTCHours(),
            M: t.getUTCMinutes(), S: t.getUTCSeconds(), ms: t.getUTCMilliseconds(), wd: t.getUTCDay()
        };
    }

    function zoneText(minutes) {
        var sign = minutes < 0 ? '-' : '+';
        var abs = Math.abs(minutes);
        return sign + pad(Math.floor(abs / 60), 2) + ':' + pad(abs % 60, 2);
    }

    function dateToString(v) {
        if (v.kind === 'date') { return pad(v.y, 4) + '-' + pad(v.m, 2) + '-' + pad(v.d, 2); }
        var t = new Date(v.ms + v.offset * 60000);
        return pad(t.getUTCFullYear(), 4) + '-' + pad(t.getUTCMonth() + 1, 2) + '-' + pad(t.getUTCDate(), 2)
            + 'T' + pad(t.getUTCHours(), 2) + ':' + pad(t.getUTCMinutes(), 2) + ':' + pad(t.getUTCSeconds(), 2)
            + '.' + pad(t.getUTCMilliseconds(), 3) + zoneText(v.offset);
    }

    function dateFromInstant(ms) { return { date: true, kind: 'dateTime', ms: ms, offset: envOffset }; }

    function dateFromDayNumber(n) {
        var t = new Date(Math.floor(n) * DAY);
        return makeDate(t.getUTCFullYear(), t.getUTCMonth() + 1, t.getUTCDate());
    }

    // Anything a date function is given, as a date value, or null when it is not one (an empty answer included).
    function toDateValue(v) {
        if (isDate(v)) { return v; }
        if (typeof v === 'number') {
            if (isNaN(v) || !isFinite(v)) { return null; }
            return Number.isInteger(v) ? dateFromDayNumber(v) : dateFromInstant(v * DAY);
        }
        var s = stringValue(v).trim();
        if (s === '') { return null; }
        if (/^-?(\d+(\.\d*)?|\.\d+)$/.test(s)) { return toDateValue(parseFloat(s)); }
        return parseDateLike(s);
    }

    var MONTHS = ['Jan', 'Feb', 'Mar', 'Apr', 'May', 'Jun', 'Jul', 'Aug', 'Sep', 'Oct', 'Nov', 'Dec'];
    var DAYS = ['Sun', 'Mon', 'Tue', 'Wed', 'Thu', 'Fri', 'Sat'];

    // The specification lets %b and %a follow the form's locale. INFERRED: English unless the context names a
    // language Intl knows, because a test and an offline device must give the same answer without a locale table.
    function shortName(kind, f, language) {
        if (language) {
            try {
                var option = kind === 'month' ? { month: 'short', timeZone: 'UTC' } : { weekday: 'short', timeZone: 'UTC' };
                return new Intl.DateTimeFormat(language, option).format(new Date(Date.UTC(f.y, f.m - 1, f.d)));
            } catch (e) { /* not a locale Intl knows: fall back to English */ }
        }
        return kind === 'month' ? MONTHS[f.m - 1] : DAYS[f.wd];
    }

    function formatDate(v, format, withTime, language) {
        var d = toDateValue(v);
        if (!d) { return ''; }
        var f = wallFields(d);
        return format.replace(/%(.)/g, function (all, token) {
            switch (token) {
                case 'Y': return pad(f.y, 4);
                case 'y': return pad(f.y % 100, 2);
                case 'm': return pad(f.m, 2);
                case 'n': return String(f.m);
                case 'b': return shortName('month', f, language);
                case 'd': return pad(f.d, 2);
                case 'e': return String(f.d);
                case 'a': return shortName('day', f, language);
                case '%': return '%';
                default:
                    if (withTime) {
                        switch (token) {
                            case 'H': return pad(f.H, 2);
                            case 'h': return String(f.H);
                            case 'M': return pad(f.M, 2);
                            case 'S': return pad(f.S, 2);
                            case '3': return pad(f.ms, 3);
                        }
                    }
                    return all;
            }
        });
    }

    // ---------------------------------------------------------------------------------------------------------------
    // Nodes. Three kinds: the form's own (answers, repeat instances, and the group a question sits in), and a choice
    // list read through instance(). Each knows its value, its parent, its position, and how to find children by name.
    // ---------------------------------------------------------------------------------------------------------------

    function answerNodes(raw, parent) {
        if (raw === null || raw === undefined) { return []; }
        var list = Array.isArray(raw) ? raw : [raw];
        var nodes = [];
        for (var i = 0; i < list.length; i++) {
            var item = list[i];
            var value = item !== null && typeof item === 'object' ? (item.value === undefined ? '' : item.value) : item;
            nodes.push({ value: value === null ? '' : value, parent: parent, position: i + 1, children: none });
        }
        return nodes;
    }

    function none() { return []; }

    // INFERRED: a path to a form question (/data/group/name, ../name) is resolved by its last name, as ${name} is.
    // XLSForm requires names to be unique within a form, so the last step identifies the node; the groups on the way
    // are where it sits, which the caller's value() already knows.
    function formNodes(env, name) {
        return answerNodes(env.ctx.value(name), env.group);
    }

    function instanceRoot(env, id, at) {
        var rows = typeof env.ctx.choices === 'function' ? env.ctx.choices(id) : null;
        if (!Array.isArray(rows)) {
            fail('instance(\'' + id + '\') is not a choice list of this form; only choice lists are in the first version', at);
        }
        var doc = { value: '', position: 1 };
        var root = { value: '', parent: doc, position: 1 };
        var items = [];
        for (var i = 0; i < rows.length; i++) { items.push(itemNode(rows[i], root, i + 1)); }
        doc.parent = doc;
        doc.children = function (name) { return name === 'root' || name === '*' ? [root] : []; };
        doc.descendants = function (name) {
            if (name === 'root') { return [root]; }
            if (name === 'item') { return items; }
            var found = [];
            for (var j = 0; j < items.length; j++) { found = found.concat(items[j].children(name)); }
            return found;
        };
        root.children = function (name) { return name === 'item' || name === '*' ? items : []; };
        root.descendants = function (name) { return doc.descendants(name).filter(function (n) { return n !== root; }); };
        return doc;
    }

    function itemNode(row, parent, position) {
        var node = { value: '', parent: parent, position: position, row: row };
        node.children = function (name) {
            var out = [];
            var keys = name === '*' ? Object.keys(row) : (Object.prototype.hasOwnProperty.call(row, name) ? [name] : []);
            for (var i = 0; i < keys.length; i++) {
                var v = row[keys[i]];
                out.push({ value: v === null || v === undefined ? '' : String(v), parent: node, position: i + 1, children: none });
            }
            return out;
        };
        node.descendants = node.children;
        return node;
    }

    // ---------------------------------------------------------------------------------------------------------------
    // Evaluation.
    // ---------------------------------------------------------------------------------------------------------------

    function compareAtoms(op, a, b) {
        switch (op) {
            case '=': return a === b;
            case '!=': return a !== b;
            case '<': return a < b;
            case '<=': return a <= b;
            case '>': return a > b;
            default: return a >= b;
        }
    }

    // XPath 1.0 §3.4, with dates: when either side is a date, both are compared as numbers (days), so that
    // ${d} = today() and ${d} < date('2024-01-01') hold whatever their text looks like.
    function compare(op, left, right) {
        var relational = op !== '=' && op !== '!=';
        if (isNodeSet(left) || isNodeSet(right)) {
            if (isNodeSet(left) && isNodeSet(right)) {
                for (var i = 0; i < left.nodes.length; i++) {
                    for (var j = 0; j < right.nodes.length; j++) {
                        var a = nodeValue(left.nodes[i]);
                        var b = nodeValue(right.nodes[j]);
                        if (relational ? compareAtoms(op, numberValue(a), numberValue(b)) : compareAtoms(op, a, b)) { return true; }
                    }
                }
                return false;
            }
            var set = isNodeSet(left) ? left : right;
            var other = isNodeSet(left) ? right : left;
            if (typeof other === 'boolean') {
                return isNodeSet(left) ? compareAtoms(op, +booleanValue(set), +other) : compareAtoms(op, +other, +booleanValue(set));
            }
            for (var k = 0; k < set.nodes.length; k++) {
                var nv = nodeValue(set.nodes[k]);
                var l = isNodeSet(left) ? nv : other;
                var r = isNodeSet(left) ? other : nv;
                if (compareScalars(op, l, r)) { return true; }
            }
            return false;
        }
        return compareScalars(op, left, right);
    }

    function compareScalars(op, a, b) {
        if (op === '=' || op === '!=') {
            if (typeof a === 'boolean' || typeof b === 'boolean') { return compareAtoms(op, booleanValue(a), booleanValue(b)); }
            if (typeof a === 'number' || typeof b === 'number' || isDate(a) || isDate(b)) {
                return compareAtoms(op, numberValue(a), numberValue(b));
            }
            return compareAtoms(op, stringValue(a), stringValue(b));
        }
        return compareAtoms(op, numberValue(a), numberValue(b));
    }

    function evalNode(ast, env) {
        switch (ast.type) {
            case 'num': return ast.value;
            case 'str': return ast.value;
            case 'ref': return nodeSet(formNodes(env, ast.name));
            case 'neg': return -numberValue(evalNode(ast.arg, env));
            case 'bin': return evalBinary(ast, env);
            case 'call': return evalCall(ast, env);
            case 'filter': return nodeSet(filterNodes(asNodes(evalNode(ast.base, env), ast.at), ast.predicates, env));
            case 'path': return nodeSet(evalPath(ast, env));
        }
        return fail('Cannot evaluate a ' + ast.type, ast.at);
    }

    function asNodes(v, at) {
        if (!isNodeSet(v)) { fail('Expected a node-set (a question or a list), not a ' + typeName(v), at); }
        return v.nodes;
    }

    function typeName(v) { return isDate(v) ? 'date' : typeof v; }

    function evalBinary(ast, env) {
        var op = ast.op;
        if (op === 'or') { return booleanValue(evalNode(ast.left, env)) || booleanValue(evalNode(ast.right, env)); }
        if (op === 'and') { return booleanValue(evalNode(ast.left, env)) && booleanValue(evalNode(ast.right, env)); }
        var left = evalNode(ast.left, env);
        var right = evalNode(ast.right, env);
        switch (op) {
            case '|': return nodeSet(asNodes(left, ast.left.at).concat(asNodes(right, ast.right.at)));
            case '+': return numberValue(left) + numberValue(right);
            case '-': return numberValue(left) - numberValue(right);
            case '*': return numberValue(left) * numberValue(right);
            case 'div': return numberValue(left) / numberValue(right);
            case 'mod': return numberValue(left) % numberValue(right); // XPath 1.0: truncating, as JavaScript's %
            default: return compare(op, left, right);
        }
    }

    function filterNodes(nodes, preds, env) {
        for (var p = 0; p < preds.length; p++) {
            var kept = [];
            for (var i = 0; i < nodes.length; i++) {
                var inner = {
                    ctx: env.ctx, node: nodes[i], dot: env.dot, group: env.group, inPredicate: true,
                    position: i + 1, size: nodes.length
                };
                var v = evalNode(preds[p], inner);
                if (typeof v === 'number' ? v === i + 1 : booleanValue(v)) { kept.push(nodes[i]); }
            }
            nodes = kept;
        }
        return nodes;
    }

    function stepFrom(node, s, env) {
        var found;
        if (s.axis === 'self') {
            found = s.name === '*' ? [node] : [];
        } else if (s.axis === 'parent') {
            found = node.parent ? [node.parent] : [];
        } else if (node.isForm) {
            found = s.name === '*' || s.name === '#text' ? [] : formNodes(env, s.name);
        } else if (s.name === '#text') {
            found = [node];
        } else if (s.descendant && node.descendants) {
            found = node.descendants(s.name);
        } else {
            found = node.children ? node.children(s.name) : [];
        }
        return found;
    }

    function evalPath(ast, env) {
        var nodes;
        if (ast.base) {
            nodes = asNodes(evalNode(ast.base, env), ast.at);
        } else if (ast.absolute) {
            // An absolute path is into the form (the primary instance), never into a choice list. Its first step
            // names the form's root element, whatever that is called, so it is the form itself.
            var steps = ast.steps;
            if (steps.length && steps[0].axis === 'child' && !steps[0].descendant) { steps = steps.slice(1); }
            return walk([env.group], steps, env);
        } else {
            nodes = [env.inPredicate ? env.node : env.dot];
        }
        return walk(nodes, ast.steps, env);
    }

    function walk(nodes, steps, env) {
        for (var i = 0; i < steps.length; i++) {
            var s = steps[i];
            var next = [];
            for (var j = 0; j < nodes.length; j++) {
                var found = stepFrom(nodes[j], s, env);
                for (var f = 0; f < found.length; f++) {
                    if (next.indexOf(found[f]) < 0) { next.push(found[f]); }
                }
            }
            nodes = s.predicates.length ? filterNodes(next, s.predicates, env) : next;
        }
        return nodes;
    }

    // ---------------------------------------------------------------------------------------------------------------
    // Function helpers.
    // ---------------------------------------------------------------------------------------------------------------

    // Every value a node-set holds, or the one value a scalar is: what concat, join, checklist and the geographic
    // functions iterate over.
    function flatValues(v) {
        if (isNodeSet(v)) { return v.nodes.map(nodeValue); }
        return [stringValue(v)];
    }

    // INFERRED, and the separator rule in the header: comma, space, or both, between choice names.
    function selectedList(v) {
        var s = stringValue(v).trim();
        return s === '' ? [] : s.split(/[\s,]+/).filter(function (x) { return x !== ''; });
    }

    // XPath 1.0 §4.4 round(): halves go up, towards positive infinity; NaN and the infinities pass through.
    function xpathRound(x) { return Math.floor(x + 0.5); }

    function roundTo(x, decimals) {
        if (isNaN(x) || !isFinite(x)) { return x; }
        if (!decimals) { return Math.floor(x + 0.5); }
        // Scale through the decimal text so that round(1.005, 2) is 1.01, as the written number says, and not 1.00.
        var scaled = Number(x + 'e' + decimals);
        if (!isFinite(scaled)) { return x; }
        return Number(Math.floor(scaled + 0.5) + 'e' + (-decimals));
    }

    // checklist: a value counts when it is true, or a number above zero — the specification's "evaluates to true
    // (when it converts to a number > 0)". INFERRED: the text 'true' also counts, because a boolean stored as an
    // answer is the text 'true'; any other text counts only if it is a positive number.
    function checks(value) {
        if (typeof value === 'boolean') { return value; }
        if (typeof value === 'number') { return value > 0; }
        var s = String(value).trim();
        return s === 'true' || numberValue(s) > 0;
    }

    function checkValues(v) {
        if (isNodeSet(v)) { return v.nodes.map(nodeValue); }
        return [v];
    }

    function within(count, min, max) {
        return (min === -1 || count >= min) && (max === -1 || count <= max);
    }

    function randomUnit(env) {
        return typeof env.ctx.random === 'function' ? env.ctx.random() : Math.random();
    }

    function uuid(env, length) {
        var hex = '0123456789abcdef';
        if (length === undefined) {
            var out = '';
            for (var i = 0; i < 36; i++) {
                if (i === 8 || i === 13 || i === 18 || i === 23) { out += '-'; } else if (i === 14) { out += '4'; } else if (i === 19) {
                    out += hex.charAt(8 + Math.floor(randomUnit(env) * 4));
                } else { out += hex.charAt(Math.floor(randomUnit(env) * 16)); }
            }
            return out;
        }
        var alphabet = 'ABCDEFGHIJKLMNOPQRSTUVWXYZabcdefghijklmnopqrstuvwxyz0123456789';
        var text = '';
        for (var j = 0; j < length; j++) { text += alphabet.charAt(Math.floor(randomUnit(env) * alphabet.length)); }
        return text;
    }

    // SHA-256, for randomize()'s seed when the seed is not a number (the specification's rule). Written from FIPS
    // 180-4; synchronous because evaluation is, which is why crypto.subtle (a promise) cannot be used.
    var K = [
        0x428a2f98, 0x71374491, 0xb5c0fbcf, 0xe9b5dba5, 0x3956c25b, 0x59f111f1, 0x923f82a4, 0xab1c5ed5, 0xd807aa98,
        0x12835b01, 0x243185be, 0x550c7dc3, 0x72be5d74, 0x80deb1fe, 0x9bdc06a7, 0xc19bf174, 0xe49b69c1, 0xefbe4786,
        0x0fc19dc6, 0x240ca1cc, 0x2de92c6f, 0x4a7484aa, 0x5cb0a9dc, 0x76f988da, 0x983e5152, 0xa831c66d, 0xb00327c8,
        0xbf597fc7, 0xc6e00bf3, 0xd5a79147, 0x06ca6351, 0x14292967, 0x27b70a85, 0x2e1b2138, 0x4d2c6dfc, 0x53380d13,
        0x650a7354, 0x766a0abb, 0x81c2c92e, 0x92722c85, 0xa2bfe8a1, 0xa81a664b, 0xc24b8b70, 0xc76c51a3, 0xd192e819,
        0xd6990624, 0xf40e3585, 0x106aa070, 0x19a4c116, 0x1e376c08, 0x2748774c, 0x34b0bcb5, 0x391c0cb3, 0x4ed8aa4a,
        0x5b9cca4f, 0x682e6ff3, 0x748f82ee, 0x78a5636f, 0x84c87814, 0x8cc70208, 0x90befffa, 0xa4506ceb, 0xbef9a3f7,
        0xc67178f2];

    function sha256(bytes) {
        var h = [0x6a09e667, 0xbb67ae85, 0x3c6ef372, 0xa54ff53a, 0x510e527f, 0x9b05688c, 0x1f83d9ab, 0x5be0cd19];
        var length = bytes.length;
        var total = ((length + 9 + 63) >> 6) << 6;
        var data = new Uint8Array(total);
        data.set(bytes);
        data[length] = 0x80;
        var bits = length * 8;
        for (var b = 0; b < 8; b++) { data[total - 1 - b] = Math.floor(bits / Math.pow(2, 8 * b)) & 0xff; }
        var w = new Array(64);
        for (var off = 0; off < total; off += 64) {
            for (var t = 0; t < 16; t++) {
                w[t] = (data[off + 4 * t] << 24) | (data[off + 4 * t + 1] << 16) | (data[off + 4 * t + 2] << 8) | data[off + 4 * t + 3];
            }
            for (t = 16; t < 64; t++) {
                var x = w[t - 15];
                var y = w[t - 2];
                var s0 = ((x >>> 7) | (x << 25)) ^ ((x >>> 18) | (x << 14)) ^ (x >>> 3);
                var s1 = ((y >>> 17) | (y << 15)) ^ ((y >>> 19) | (y << 13)) ^ (y >>> 10);
                w[t] = (w[t - 16] + s0 + w[t - 7] + s1) | 0;
            }
            var a = h[0], c1 = h[1], c2 = h[2], d = h[3], e = h[4], f = h[5], g = h[6], hh = h[7];
            for (t = 0; t < 64; t++) {
                var S1 = ((e >>> 6) | (e << 26)) ^ ((e >>> 11) | (e << 21)) ^ ((e >>> 25) | (e << 7));
                var ch = (e & f) ^ (~e & g);
                var t1 = (hh + S1 + ch + K[t] + w[t]) | 0;
                var S0 = ((a >>> 2) | (a << 30)) ^ ((a >>> 13) | (a << 19)) ^ ((a >>> 22) | (a << 10));
                var maj = (a & c1) ^ (a & c2) ^ (c1 & c2);
                var t2 = (S0 + maj) | 0;
                hh = g; g = f; f = e; e = (d + t1) | 0; d = c2; c2 = c1; c1 = a; a = (t1 + t2) | 0;
            }
            h[0] = (h[0] + a) | 0; h[1] = (h[1] + c1) | 0; h[2] = (h[2] + c2) | 0; h[3] = (h[3] + d) | 0;
            h[4] = (h[4] + e) | 0; h[5] = (h[5] + f) | 0; h[6] = (h[6] + g) | 0; h[7] = (h[7] + hh) | 0;
        }
        var out = new Uint8Array(32);
        for (var i = 0; i < 8; i++) {
            out[4 * i] = (h[i] >>> 24) & 0xff; out[4 * i + 1] = (h[i] >>> 16) & 0xff;
            out[4 * i + 2] = (h[i] >>> 8) & 0xff; out[4 * i + 3] = h[i] & 0xff;
        }
        return out;
    }

    function hex(bytes) {
        var s = '';
        for (var i = 0; i < bytes.length; i++) { s += (bytes[i] < 16 ? '0' : '') + bytes[i].toString(16); }
        return s;
    }

    // The specification's randomize(): a seeded Park–Miller generator (multiplier 16807, modulus 2^31 - 1) driving the
    // inside-out Fisher–Yates shuffle. A decimal seed is truncated; a seed that is not a number is hashed (SHA-256 of
    // its UTF-8) and the first eight bytes, read as a signed big-endian 64-bit integer, are the seed. BigInt carries
    // that integer, because a 64-bit seed does not fit a double.
    function parkMiller(seedValue) {
        var seed;
        var n = numberValue(seedValue);
        if (isNaN(n)) {
            var digest = sha256(new TextEncoder().encode(stringValue(seedValue)));
            seed = BigInt.asIntN(64, BigInt('0x' + hex(digest.subarray(0, 8))));
        } else {
            seed = BigInt(Math.trunc(n));
        }
        var modulus = 2147483647n;
        var state = Number(seed % modulus);
        if (state <= 0) { state += 2147483646; }
        return function () {
            state = (state * 16807) % 2147483647;
            return (state - 1) / 2147483646;
        };
    }

    function shuffle(items, random) {
        var result = [];
        for (var i = 0; i < items.length; i++) {
            var j = Math.floor(random() * (i + 1));
            if (j !== i) { result[i] = result[j]; }
            result[j] = items[i];
        }
        return result;
    }

    // ---------------------------------------------------------------------------------------------------------------
    // Geography. Points are "lat lon [alt [accuracy]]"; a geotrace or geoshape is points separated by ';'.
    //
    // <b>The Earth is a sphere whose equator is 40,075,000 m round.</b> The specification says both area() and
    // distance() "take into account the circumference of the Earth around the Equator" and ignore altitude, and that
    // distance() uses the spherical law of cosines; it gives neither the figure nor area()'s method. INFERRED: the
    // circumference is the equatorial one to five significant figures (R = 40,075,000 / 2π ≈ 6,378,134.9 m), and
    // area() is the spherical polygon area of Chamberlain and Duquette ("Some algorithms for polygons on a sphere",
    // JPL, 2007): A = |Σ (λ₂ − λ₁)(2 + sin φ₁ + sin φ₂)| · R² / 2 — a sphere's area to match a sphere's distance, so
    // that a square's sides and its area agree with each other. Neither is geodesic on the WGS84 ellipsoid; the
    // difference is up to about half a percent, and matching what the specification describes is what condition 4
    // asks for.
    // ---------------------------------------------------------------------------------------------------------------

    var RADIUS = 40075000 / (2 * Math.PI);

    function parsePoint(text) {
        var parts = String(text).trim().split(/\s+/);
        if (parts.length < 2) { return null; }
        var lat = parseFloat(parts[0]);
        var lon = parseFloat(parts[1]);
        if (isNaN(lat) || isNaN(lon) || Math.abs(lat) > 90 || Math.abs(lon) > 180) { return null; }
        return { lat: lat, lon: lon };
    }

    // Points from a value: a node-set of geopoints, or one geotrace/geoshape/geopoint text. null if any is malformed.
    function pointsOf(v) {
        var texts = [];
        var values = flatValues(v);
        for (var i = 0; i < values.length; i++) {
            if (values[i].trim() === '') { continue; }
            var pieces = values[i].split(';');
            for (var j = 0; j < pieces.length; j++) {
                if (pieces[j].trim() !== '') { texts.push(pieces[j]); }
            }
        }
        var points = [];
        for (var k = 0; k < texts.length; k++) {
            var p = parsePoint(texts[k]);
            if (!p) { return null; }
            points.push(p);
        }
        return points;
    }

    function rad(deg) { return deg * Math.PI / 180; }

    function segmentLength(a, b) {
        var cosine = Math.sin(rad(a.lat)) * Math.sin(rad(b.lat))
            + Math.cos(rad(a.lat)) * Math.cos(rad(b.lat)) * Math.cos(rad(b.lon - a.lon));
        return RADIUS * Math.acos(Math.max(-1, Math.min(1, cosine)));
    }

    function polygonArea(points) {
        if (points.length < 3) { return 0; }
        var sum = 0;
        for (var i = 0; i < points.length; i++) {
            var a = points[i];
            var b = points[(i + 1) % points.length];
            sum += rad(b.lon - a.lon) * (2 + Math.sin(rad(a.lat)) + Math.sin(rad(b.lat)));
        }
        return Math.abs(sum * RADIUS * RADIUS / 2);
    }

    // geofence: the even–odd ray test on longitude (x) and latitude (y), which is the algorithm the specification
    // links to (W. R. Franklin's PNPOLY), on the plane of the coordinates as written.
    function inside(point, shape) {
        var result = false;
        for (var i = 0, j = shape.length - 1; i < shape.length; j = i++) {
            var yi = shape[i].lat, xi = shape[i].lon, yj = shape[j].lat, xj = shape[j].lon;
            if (((yi > point.lat) !== (yj > point.lat))
                && (point.lon < (xj - xi) * (point.lat - yi) / (yj - yi) + xi)) {
                result = !result;
            }
        }
        return result;
    }

    function orientation(p, q, r) {
        var v = (q.lat - p.lat) * (r.lon - q.lon) - (q.lon - p.lon) * (r.lat - q.lat);
        return v === 0 ? 0 : (v > 0 ? 1 : 2);
    }

    function onSegment(p, q, r) {
        return q.lon <= Math.max(p.lon, r.lon) && q.lon >= Math.min(p.lon, r.lon)
            && q.lat <= Math.max(p.lat, r.lat) && q.lat >= Math.min(p.lat, r.lat);
    }

    function segmentsCross(p1, q1, p2, q2) {
        var o1 = orientation(p1, q1, p2), o2 = orientation(p1, q1, q2);
        var o3 = orientation(p2, q2, p1), o4 = orientation(p2, q2, q1);
        if (o1 !== o2 && o3 !== o4) { return true; }
        return (o1 === 0 && onSegment(p1, p2, q1)) || (o2 === 0 && onSegment(p1, q2, q1))
            || (o3 === 0 && onSegment(p2, p1, q2)) || (o4 === 0 && onSegment(p2, q1, q2));
    }

    // intersects: any two segments that are not consecutive cross or touch. A closed line's first and last segments
    // share the closing point and so are consecutive too.
    function selfIntersects(points) {
        var n = points.length - 1; // segments
        var closed = n > 2 && points[0].lat === points[n].lat && points[0].lon === points[n].lon;
        for (var i = 0; i < n; i++) {
            for (var j = i + 2; j < n; j++) {
                if (closed && i === 0 && j === n - 1) { continue; }
                if (segmentsCross(points[i], points[i + 1], points[j], points[j + 1])) { return true; }
            }
        }
        return false;
    }

    // ---------------------------------------------------------------------------------------------------------------
    // Functions.
    // ---------------------------------------------------------------------------------------------------------------

    function lastName(ast) {
        if (ast.type === 'ref') { return ast.name; }
        if (ast.type === 'path' && ast.steps.length) {
            var s = ast.steps[ast.steps.length - 1];
            if (s.axis === 'child' && s.name !== '*') { return s.name; }
        }
        return null;
    }

    function choiceLabel(row, language) {
        if (language && row['label::' + language] !== undefined) { return String(row['label::' + language]); }
        if (row.label !== undefined) { return String(row.label); }
        var keys = Object.keys(row);
        for (var i = 0; i < keys.length; i++) {
            if (keys[i].indexOf('label::') === 0) { return String(row[keys[i]]); }
        }
        return '';
    }

    // pulldata("@property", name) and property(): the answer comes from the page (ctx.property), which knows who is signed in
    // and what the device says; an anonymous form or an unavailable value is '' (INFERRED, section 5.20). A boolean stays a
    // boolean, so if(pulldata("@property", 'online'), ...) means what it says.
    function propertyValue(name, ctx) {
        if (!listHas(PROPERTIES, name)) { return fail('\'' + name + '\' is not a property this client can give'); }
        if (typeof ctx.property !== 'function') { return ''; }
        var got = ctx.property(name);
        return got === null || got === undefined ? '' : got;
    }

    // pulldata("@geopoint", point, part). A point is "latitude longitude altitude accuracy", as the client keeps it. What it
    // did not record answers '': a position typed or placed by hand has no accuracy and no altitude (INFERRED, section
    // 5.20: the documentation does not say what a missing value answers).
    function geopointPart(point, part) {
        var tokens = point.trim().split(/\s+/).filter(Boolean);
        if (tokens.length < 2) { return ''; }
        var lat = Number(tokens[0]), lon = Number(tokens[1]);
        if (isNaN(lat) || isNaN(lon)) { return ''; }
        var altitude = tokens.length > 2 ? Number(tokens[2]) : NaN;
        var accuracy = tokens.length > 3 ? Number(tokens[3]) : NaN;
        var recorded = accuracy > 0;      // a device reading; a typed or placed point is stored with accuracy 0
        switch (part) {
            case 'x': return lon;
            case 'y': return lat;
            case 'z': return recorded && !isNaN(altitude) ? altitude : '';
            case 'horizontalAccuracy':
            case 'accuracy': return recorded ? accuracy : '';
            case 'positionSourceType': return recorded ? 2 : 1;   // System Location (2), User (1)
        }
        if (part === 'DMS' || part.indexOf('DMS.') === 0) {
            var la = dms(lat, 'N', 'S'), lo = dms(lon, 'E', 'W');
            var parts = {
                latitudeDegrees: la.d, latitudeHemisphere: la.h, latitudeMinutes: la.m, latitudeSeconds: la.s, latitudeText: la.text,
                longitudeDegrees: lo.d, longitudeHemisphere: lo.h, longitudeMinutes: lo.m, longitudeSeconds: lo.s, longitudeText: lo.text,
                text: la.whole + ' ' + lo.whole
            };
            if (part === 'DMS') { return JSON.stringify(parts); }
            var key = part.substring(4);
            return hasKey(parts, key) ? parts[key] : '';
        }
        return '';
    }

    // Degrees, minutes and seconds of one coordinate. Seconds are rounded to a tenth for the parts, as the documented example
    // shows (36.3, 4), and to a whole second for the whole-point text; a rounding that reaches 60 carries.
    function dms(value, positive, negative) {
        var h = value < 0 ? negative : positive;
        function split(places) {
            var unit = Math.pow(10, places);
            var total = Math.round(Math.abs(value) * 3600 * unit);
            var d = Math.floor(total / (3600 * unit));
            var rest = total - d * 3600 * unit;
            var m = Math.floor(rest / (60 * unit));
            var s = (rest - m * 60 * unit) / unit;
            return { d: d, m: m, s: s };
        }
        var tenth = split(1), whole = split(0);
        return {
            d: tenth.d, m: tenth.m, s: tenth.s, h: h,
            text: tenth.d + '\u00b0' + tenth.m + '\'' + tenth.s.toFixed(1) + '"' + h,
            whole: whole.d + '\u00b0' + whole.m + '\'' + whole.s + '"' + h
        };
    }

    function evalCall(ast, env) {
        var args = ast.args;
        var ctx = env.ctx;
        function arg(i) { return evalNode(args[i], env); }
        function str(i) { return stringValue(arg(i)); }
        function num(i) { return numberValue(arg(i)); }
        function current() { return env.dot; }

        switch (ast.name) {
            // ----- String
            case 'string': return args.length ? str(0) : nodeValue(current());
            case 'concat': {
                var text = '';
                for (var i = 0; i < args.length; i++) { text += flatValues(arg(i)).join(''); }
                return text;
            }
            case 'join': {
                var separator = str(0);
                var parts = [];
                for (var j = 1; j < args.length; j++) { parts = parts.concat(flatValues(arg(j))); }
                return parts.join(separator);
            }
            case 'substr': {
                var s = str(0);
                var start = Math.max(0, Math.floor(num(1)));
                var end = args.length > 2 ? Math.min(s.length, Math.floor(num(2))) : s.length;
                if (isNaN(start)) { start = 0; }
                return isNaN(end) || end <= start ? '' : s.substring(start, end);
            }
            case 'substring-before': {
                var a = str(0), b = str(1), at = a.indexOf(b);
                return at < 0 ? '' : a.substring(0, at);
            }
            case 'substring-after': {
                var a2 = str(0), b2 = str(1), at2 = a2.indexOf(b2);
                return at2 < 0 ? '' : a2.substring(at2 + b2.length);
            }
            case 'translate': {
                var source = Array.from(str(0)), from = Array.from(str(1)), to = Array.from(str(2));
                return source.map(function (ch) {
                    var index = from.indexOf(ch);
                    return index < 0 ? ch : (index < to.length ? to[index] : '');
                }).join('');
            }
            case 'string-length': return Array.from(str(0)).length; // XPath 1.0 counts characters; ODK makes the argument required
            case 'normalize-space': return (args.length ? str(0) : nodeValue(current())).replace(/[ \t\r\n]+/g, ' ').trim();
            case 'substring': {
                // XPath 1.0 §4.2: the characters whose 1-based position p satisfies round(start) <= p < round(start) + round(length).
                // With no length the run goes to the end. A NaN start or length selects nothing, as the specification says.
                var chars = Array.from(str(0));
                var from = xpathRound(num(1));
                var to = args.length > 2 ? from + xpathRound(num(2)) : Infinity;
                var kept = '';
                for (var ci = 0; ci < chars.length; ci++) {
                    if (ci + 1 >= from && ci + 1 < to) { kept += chars[ci]; }
                }
                return kept;
            }
            case 'contains': return str(0).indexOf(str(1)) >= 0;
            case 'starts-with': return str(0).indexOf(str(1)) === 0;
            case 'ends-with': {
                var whole = str(0), tail = str(1);
                return whole.length >= tail.length && whole.substring(whole.length - tail.length) === tail;
            }
            case 'uuid': return args.length ? uuid(env, Math.max(0, Math.floor(num(0)))) : uuid(env);
            case 'pulldata': {
                var file = str(0);
                if (file === '@property') { return propertyValue(str(1), ctx); }
                if (file === '@geopoint') { return geopointPart(str(1), str(2)); }
                if (file.charAt(0) === '@') {
                    fail('pulldata("' + file + '") is not supported: ' + (hasKey(REFUSED_PULLDATA_SOURCES, file)
                        ? REFUSED_PULLDATA_SOURCES[file] : 'it is not a source Survey123 documents'), ast.at);
                }
                if (args.length !== 4) { fail('pulldata() takes 4 arguments, not ' + args.length, ast.at); }
                if (typeof ctx.pulldata !== 'function') { return ''; }
                var got = ctx.pulldata(file, str(1), str(2), str(3));
                return got === null || got === undefined ? '' : String(got);
            }

            case 'property': {
                var written = str(0);
                var odk = written.indexOf('uri:') === 0 ? written.substring(4) : written;
                if (hasKey(ODK_PROPERTIES, odk)) { return propertyValue(ODK_PROPERTIES[odk], ctx); }
                if (!listHas(PROPERTIES, odk)) { return fail('property(\'' + written + '\') is not supported', ast.at); }
                return propertyValue(odk, ctx);
            }

            // ----- Boolean
            case 'if': return booleanValue(arg(0)) ? arg(1) : arg(2);
            case 'coalesce': {
                var first = arg(0);
                if (stringValue(first) !== '') { return first; }
                return arg(1);
            }
            case 'once': {
                var own = nodeValue(current());
                return own !== '' ? own : arg(0);
            }
            case 'true': return true;
            case 'false': return false;
            case 'boolean': return booleanValue(arg(0));
            case 'boolean-from-string': {
                var flag = str(0);
                return flag === 'true' || flag === '1';
            }
            case 'not': return !booleanValue(arg(0));
            case 'regex': {
                var pattern;
                try { pattern = new RegExp(str(1)); } catch (e) { return fail('regex(): \'' + str(1) + '\' is not a regular expression', ast.at); }
                return pattern.test(str(0));
            }
            case 'checklist': {
                var count = 0;
                for (var c = 2; c < args.length; c++) {
                    var vals = checkValues(arg(c));
                    for (var v = 0; v < vals.length; v++) { if (checks(vals[v])) { count++; } }
                }
                return within(count, num(0), num(1));
            }
            case 'weighted-checklist': {
                var total = 0;
                for (var w = 2; w < args.length; w += 2) {
                    var answers = checkValues(arg(w));
                    var weight = num(w + 1);
                    for (var q = 0; q < answers.length; q++) { if (checks(answers[q])) { total += weight; } }
                }
                return within(total, num(0), num(1));
            }

            // ----- Number
            case 'number': return args.length ? num(0) : numberValue(nodeValue(current()));
            case 'random': return randomUnit(env);
            case 'int': {
                var n = num(0);
                return isFinite(n) ? Math.trunc(n) : n;
            }
            case 'sum': {
                var nodes = asNodes(arg(0), args[0].at);
                var sum = 0;
                for (var si = 0; si < nodes.length; si++) {
                    var nv = nodeValue(nodes[si]);
                    sum += nv.trim() === '' ? 0 : numberValue(nv); // the specification: empty counts as 0, not NaN
                }
                return sum;
            }
            case 'max':
            case 'min': {
                var numbers = [];
                for (var mi = 0; mi < args.length; mi++) {
                    var value = arg(mi);
                    if (isNodeSet(value)) {
                        for (var mj = 0; mj < value.nodes.length; mj++) { numbers.push(numberValue(nodeValue(value.nodes[mj]))); }
                    } else {
                        numbers.push(numberValue(value));
                    }
                }
                if (!numbers.length) { return NaN; }
                return ast.name === 'max' ? Math.max.apply(null, numbers) : Math.min.apply(null, numbers);
            }
            case 'round': return roundTo(num(0), args.length > 1 ? Math.trunc(num(1)) : 0);
            case 'pow': return Math.pow(num(0), num(1));
            case 'log': return Math.log(num(0));
            case 'log10': return Math.log10(num(0));
            case 'abs': return Math.abs(num(0));
            case 'sin': return Math.sin(num(0));
            case 'cos': return Math.cos(num(0));
            case 'tan': return Math.tan(num(0));
            case 'asin': return Math.asin(num(0));
            case 'acos': return Math.acos(num(0));
            case 'atan': return Math.atan(num(0));
            case 'atan2': return Math.atan2(num(0), num(1));
            case 'sqrt': return Math.sqrt(num(0));
            case 'exp': return Math.exp(num(0));
            case 'exp10': return Math.pow(10, num(0));
            case 'pi': return Math.PI;
            case 'floor': return Math.floor(num(0)); // XPath 1.0 §4.4
            case 'ceiling': return Math.ceil(num(0)); // XPath 1.0 §4.4

            // ----- Node-set
            case 'count': return asNodes(arg(0), args[0].at).length;
            case 'count-non-empty': {
                var all = asNodes(arg(0), args[0].at);
                return all.filter(function (node) { return nodeValue(node) !== ''; }).length;
            }
            case 'position': {
                if (!args.length) {
                    // Inside a predicate, the candidate's position; elsewhere, the repeat instance's.
                    return env.inPredicate ? env.position : (typeof ctx.position === 'number' ? ctx.position : 1);
                }
                var target = asNodes(arg(0), args[0].at);
                if (target.length !== 1) { return fail('position() takes a single node', ast.at); }
                return target[0].position || 1;
            }
            case 'current': return nodeSet([current()]);
            case 'randomize': {
                var list = asNodes(arg(0), args[0].at);
                var generator = args.length > 1 ? parkMiller(arg(1)) : function () { return randomUnit(env); };
                return nodeSet(shuffle(list, generator));
            }
            case 'instance': return nodeSet([instanceRoot(env, str(0), ast.at)]);
            case 'last': {
                // XPath 1.0 §4.1: the context size. Inside a predicate that is the number of nodes being filtered. Elsewhere
                // a form's expression has one context node, so it is 1; a repeat's instances are not a context size here.
                return env.inPredicate ? env.size : 1;
            }

            // ----- Date and time
            case 'today': {
                var nowAt = ctx.now ? ctx.now() : new Date();
                var local = new Date(nowAt.getTime() + envOffset * 60000);
                return makeDate(local.getUTCFullYear(), local.getUTCMonth() + 1, local.getUTCDate());
            }
            case 'now': return dateFromInstant((ctx.now ? ctx.now() : new Date()).getTime());
            case 'format-date': return formatDate(arg(0), str(1), false, ctx.language);
            case 'format-date-time': return formatDate(arg(0), str(1), true, ctx.language);
            case 'date': {
                var d = toDateValue(arg(0));
                if (!d) { return ''; }
                if (d.kind === 'date') { return d; }
                var f = wallFields(d);
                return makeDate(f.y, f.m, f.d);
            }
            case 'date-time': {
                // INFERRED (ADR-287 section 5.20): Survey123 says only "converts a number or string to a date object". A
                // number is days since 1970-01-01 UTC, so date-time(decimal-date-time(x)) is x; a date alone is the
                // device's midnight.
                var raw = arg(0);
                var asNumber = typeof raw === 'number' ? raw : (typeof raw === 'string' && /^-?(\d+(\.\d*)?|\.\d+)$/.test(raw.trim()) ? parseFloat(raw) : null);
                if (asNumber !== null) { return isNaN(asNumber) || !isFinite(asNumber) ? '' : dateFromInstant(asNumber * DAY); }
                var made = toDateValue(raw);
                if (!made) { return ''; }
                if (made.kind === 'date') { return dateFromInstant(Date.UTC(made.y, made.m - 1, made.d) - envOffset * 60000); }
                return made;
            }
            case 'decimal-date-time': {
                var dt = toDateValue(arg(0));
                return dt ? dateToDays(dt) : NaN;
            }
            case 'decimal-time': {
                var t = str(0).trim();
                var m = TIME_RE.exec(t);
                var minutes, seconds;
                if (m) {
                    if (+m[1] > 23 || +m[2] > 59 || (m[3] && +m[3] > 59)) { return NaN; }
                    seconds = (+m[1]) * 3600 + (+m[2]) * 60 + (m[3] ? +m[3] : 0) + millis(m[4]) / 1000;
                    // A time written with an offset is moved into the device's, as the specification says.
                    minutes = m[5] ? envOffset - offsetMinutes(m[5]) : 0;
                } else {
                    var dtv = parseDateLike(t);
                    if (!dtv || dtv.kind !== 'dateTime') { return NaN; }
                    var wf = wallFields(dtv);
                    seconds = wf.H * 3600 + wf.M * 60 + wf.S + wf.ms / 1000;
                    minutes = 0;
                }
                var dayFraction = (seconds + minutes * 60) / 86400;
                return ((dayFraction % 1) + 1) % 1;
            }

            // ----- Select
            case 'selected': return selectedList(arg(0)).indexOf(str(1).trim()) >= 0;
            case 'selected-at': {
                var items = selectedList(arg(0));
                var index = num(1);
                return index >= 0 && index < items.length && Number.isInteger(index) ? items[index] : '';
            }
            case 'count-selected': return selectedList(arg(0)).length;
            case 'jr:choice-name': {
                var choice = str(0).trim();
                var reference = str(1).trim();
                var named = /^\$\{\s*([^}\s]+)\s*\}$/.exec(reference);
                var question = named ? named[1] : reference.split('/').filter(Boolean).pop();
                if (!question || choice === '') { return ''; }
                var listName = typeof ctx.listOf === 'function' ? ctx.listOf(question) : null;
                var rows = listName && typeof ctx.choices === 'function' ? ctx.choices(listName) : null;
                if (!Array.isArray(rows)) { return ''; }
                for (var r = 0; r < rows.length; r++) {
                    if (String(rows[r].name) === choice) { return choiceLabel(rows[r], ctx.language); }
                }
                return '';
            }

            // ----- Repeat
            case 'indexed-repeat': {
                var name = lastName(args[0]);
                if (!name) { return fail('indexed-repeat() takes a question as its first argument', args[0].at); }
                var path = [];
                for (var p = 1; p < args.length; p += 2) {
                    var repeat = lastName(args[p]);
                    if (!repeat) { return fail('indexed-repeat() takes a repeat where argument ' + (p + 1) + ' is', args[p].at); }
                    path.push({ repeat: repeat, index: num(p + 1) });
                }
                return nodeSet(answerNodes(ctx.value(name, path), env.group).slice(0, 1));
            }

            // ----- Geographic
            case 'area': {
                var shape = pointsOf(arg(0));
                return shape ? polygonArea(shape) : NaN;
            }
            case 'distance': {
                var line = [];
                for (var g = 0; g < args.length; g++) {
                    var pts = pointsOf(arg(g));
                    if (!pts) { return NaN; }
                    line = line.concat(pts);
                }
                var length = 0;
                for (var s2 = 1; s2 < line.length; s2++) { length += segmentLength(line[s2 - 1], line[s2]); }
                return length;
            }
            case 'geofence': {
                var point = pointsOf(arg(0));
                var fence = pointsOf(arg(1));
                if (!point || point.length !== 1 || !fence || fence.length < 3) { return false; }
                return inside(point[0], fence);
            }
            case 'intersects': {
                var trace = pointsOf(arg(0));
                return !!trace && trace.length > 3 && selfIntersects(trace);
            }
        }
        return fail('Unknown function \'' + ast.name + '\'', ast.at);
    }

    // ---------------------------------------------------------------------------------------------------------------
    // The surface.
    // ---------------------------------------------------------------------------------------------------------------

    function environment(context) {
        var ctx = context || {};
        if (typeof ctx.value !== 'function') {
            var empty = ctx;
            ctx = Object.assign({}, empty, { value: function () { return null; } });
        }
        var group = { value: '', position: typeof ctx.position === 'number' ? ctx.position : 1, isForm: true, children: none };
        group.parent = group;
        var dot = {
            value: ctx.current === undefined || ctx.current === null ? '' : ctx.current,
            parent: group, position: group.position, children: none
        };
        return { ctx: ctx, group: group, dot: dot, node: dot, inPredicate: false, position: group.position, size: 1 };
    }

    function evaluate(expression, context) {
        var ast = typeof expression === 'string' ? parse(expression) : expression;
        var ctx = context || {};
        var previous = envOffset;
        envOffset = typeof ctx.timezoneOffset === 'number' ? ctx.timezoneOffset
            : -(ctx.now ? ctx.now() : new Date()).getTimezoneOffset();
        try {
            return evalNode(ast, environment(ctx));
        } finally {
            envOffset = previous;
        }
    }

    // The ${name} references an expression reads, each once, in order: what a client recalculates on.
    function references(expression) {
        var ast = typeof expression === 'string' ? parse(expression) : expression;
        var seen = [];
        (function visit(node) {
            if (!node || typeof node !== 'object') { return; }
            if (node.type === 'ref' && seen.indexOf(node.name) < 0) { seen.push(node.name); }
            var keys = Object.keys(node);
            for (var i = 0; i < keys.length; i++) {
                var child = node[keys[i]];
                if (Array.isArray(child)) { child.forEach(visit); } else if (child && typeof child === 'object') { visit(child); }
            }
        })(ast);
        return seen;
    }

    function withOffset(context, fn) {
        var ctx = context || {};
        var previous = envOffset;
        envOffset = typeof ctx.timezoneOffset === 'number' ? ctx.timezoneOffset
            : -(ctx.now ? ctx.now() : new Date()).getTimezoneOffset();
        try { return fn(); } finally { envOffset = previous; }
    }

    window.GraticulaXlsform = Object.freeze({
        /** The functions an expression may call: FormCatalog.Functions, which the tests compare it with. */
        functions: Object.freeze(Object.keys(ARITY).sort()),
        parse: parse,
        evaluate: evaluate,
        evaluateString: function (expression, context) {
            return withOffset(context, function () { return stringValue(evaluate(expression, context)); });
        },
        evaluateNumber: function (expression, context) {
            return withOffset(context, function () { return numberValue(evaluate(expression, context)); });
        },
        evaluateBoolean: function (expression, context) {
            return withOffset(context, function () { return booleanValue(evaluate(expression, context)); });
        },
        refused: Object.freeze(Object.keys(REFUSED).sort()),
        /** The lists ADR-287 section 5.20 adds, which XlsformEvaluatorTests compares with FormCatalog's. */
        catalog: Object.freeze({
            properties: PROPERTIES.slice().sort(),
            refusedProperties: Object.keys(REFUSED_PROPERTIES).sort(),
            odkProperties: Object.keys(ODK_PROPERTIES).sort(),
            refusedOdkProperties: Object.keys(REFUSED_ODK_PROPERTIES).sort(),
            geopointParts: GEOPOINT_PARTS.slice().sort(),
            geopointPartsAnsweredEmpty: GEOPOINT_PARTS_EMPTY.slice().sort(),
            refusedGeopointParts: Object.keys(REFUSED_GEOPOINT_PARTS).sort(),
            refusedPulldataSources: Object.keys(REFUSED_PULLDATA_SOURCES).sort(),
            calculationModes: CALCULATION_MODES.slice().sort()
        }),
        references: references,
        toString: stringValue,
        toNumber: numberValue,
        toBoolean: booleanValue
    });
})();
