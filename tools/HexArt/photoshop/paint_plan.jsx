// Paints one plan made by tools/HexArt/plan.py in Photoshop with the brushes already loaded in
// the program (the owner's Kyle Webster set), in a NEW document that is closed without saving
// after a PNG copy is written. Nothing of the owner's documents or presets is touched; the ruler
// unit, foreground colour, tool and brush that the plan changes are put back at the end.
// Run: Photoshop COM DoJavaScriptFile with the global HEXART_PLAN set to the plan file path, or
// `photoshop.exe -r paint_plan.jsx` after editing PLAN_FILE below.

#target photoshop

(function () {
    var planFile = (typeof HEXART_PLAN !== 'undefined') ? HEXART_PLAN : $.getenv('HEXART_PLAN');
    var plan = $.evalFile(new File(planFile));

    var sID = stringIDToTypeID, cID = charIDToTypeID;
    var oldUnits = app.preferences.rulerUnits;
    var oldColor = app.foregroundColor;
    var oldTool = app.currentTool;
    var log = [];
    app.preferences.rulerUnits = Units.PIXELS;
    var doc = null;
    try {
        doc = app.documents.add(plan.w, plan.h, 72, 'hexart-' + plan.name, NewDocumentMode.RGB, DocumentFill.TRANSPARENT);
        app.currentTool = 'paintbrushTool';
        var layers = {};
        function layer(name) {
            if (!layers[name]) {
                layers[name] = doc.artLayers.add();
                layers[name].name = name;
            }
            doc.activeLayer = layers[name];
        }
        // Brush presets are picked by their place in the Brushes panel (selecting by name is not
        // available in Photoshop 2026); the plan names them, the index is looked up here.
        var presetNames = (function () {
            var ref = new ActionReference();
            ref.putProperty(sID('property'), sID('presetManager'));
            ref.putEnumerated(cID('capp'), cID('Ordn'), cID('Trgt'));
            var list = executeActionGet(ref).getList(sID('presetManager')).getObjectValue(0).getList(cID('Nm  '));
            var names = [];
            for (var i = 0; i < list.count; i++) names.push(list.getString(i));
            return names;
        })();
        function brush(name) {
            var index = -1;
            if (typeof name === 'number') index = name;
            else for (var i = 0; i < presetNames.length; i++) if (presetNames[i] === name) index = i;
            if (index < 0) throw new Error('no brush preset ' + name);
            var r = new ActionReference();
            r.putIndex(sID('brush'), index + 1);
            var d = new ActionDescriptor();
            d.putReference(cID('null'), r);
            try {
                executeAction(cID('slct'), d, DialogModes.NO);
                return true;
            } catch (e) {
                log.push('brush ' + index + ' not selectable');
                return false;
            }
        }
        function size(px) {
            var d = new ActionDescriptor();
            var r = new ActionReference();
            r.putEnumerated(cID('Brsh'), cID('Ordn'), cID('Trgt'));
            d.putReference(cID('null'), r);
            var b = new ActionDescriptor();
            b.putUnitDouble(sID('masterDiameter'), cID('#Pxl'), px);
            d.putObject(cID('T   '), cID('Brsh'), b);
            executeAction(cID('setd'), d, DialogModes.NO);
        }
        function opacity(pc) {
            var d = new ActionDescriptor();
            var r = new ActionReference();
            r.putClass(sID('paintbrushTool'));
            d.putReference(cID('null'), r);
            var o = new ActionDescriptor();
            o.putUnitDouble(cID('Opct'), cID('#Prc'), pc);
            d.putObject(cID('T   '), cID('null'), o);
            executeAction(cID('setd'), d, DialogModes.NO);
        }
        function colour(hex) {
            var c = new SolidColor();
            c.rgb.hexValue = hex;
            app.foregroundColor = c;
            return c;
        }
        // polys: outlines first, then holes ({p: points, hole: bool}).
        function select(polys) {
            doc.selection.deselect();
            var first = true;
            for (var i = 0; i < polys.length; i++) {
                var mode = polys[i].hole ? SelectionType.DIMINISH : (first ? SelectionType.REPLACE : SelectionType.EXTEND);
                if (polys[i].hole && first) continue;
                doc.selection.select(polys[i].p, mode, 0, true);
                first = false;
            }
        }
        function strokes(lines, pressure) {
            var subs = [];
            for (var i = 0; i < lines.length; i++) {
                var pts = [];
                for (var j = 0; j < lines[i].length; j++) {
                    var p = new PathPointInfo();
                    p.kind = PointKind.CORNERPOINT;
                    p.anchor = lines[i][j];
                    p.leftDirection = lines[i][j];
                    p.rightDirection = lines[i][j];
                    pts.push(p);
                }
                if (pts.length < 2) continue;
                var sp = new SubPathInfo();
                sp.operation = ShapeOperation.SHAPEXOR;
                sp.closed = false;
                sp.entireSubPath = pts;
                subs.push(sp);
            }
            if (subs.length === 0) return;
            var path = doc.pathItems.add('hexart-stroke', subs);
            path.strokePath(ToolType.BRUSH, pressure);
            path.remove();
        }

        for (var k = 0; k < plan.ops.length; k++) {
            var op = plan.ops[k];
            if (op.layer) layer(op.layer);
            if (op.op === 'fill') {
                select(op.polys);
                doc.selection.fill(colour(op.color), ColorBlendMode.NORMAL, op.opacity || 100);
                doc.selection.deselect();
            } else if (op.op === 'paint') {
                if (op.polys) select(op.polys); else doc.selection.deselect();
                if (!brush(op.brush)) { doc.selection.deselect(); continue; }
                size(op.size);
                opacity(op.opacity || 100);
                colour(op.color);
                strokes(op.lines, op.pressure !== false);
                doc.selection.deselect();
            } else if (op.op === 'layerOpacity') {
                doc.activeLayer.opacity = op.value;
            } else if (op.op === 'blend') {
                doc.activeLayer.blendMode = eval('BlendMode.' + op.value);
            }
        }
        var png = new PNGSaveOptions();
        png.compression = 6;
        doc.saveAs(new File(plan.out), png, true, Extension.LOWERCASE);
        log.push('ok ' + plan.name);
    } catch (e) {
        log.push('error ' + plan.name + ' line ' + e.line + ': ' + e);
    } finally {
        if (doc !== null) doc.close(SaveOptions.DONOTSAVECHANGES);
        app.preferences.rulerUnits = oldUnits;
        app.foregroundColor = oldColor;
        try { app.currentTool = oldTool; } catch (ignored) {}
    }
    var f = new File(plan.out + '.log');
    f.open('w');
    f.write(log.join('\n'));
    f.close();
})();
