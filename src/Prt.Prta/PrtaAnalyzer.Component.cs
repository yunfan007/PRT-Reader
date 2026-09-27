namespace Prt.Prta.Compiler;

internal static partial class PrtaAnalyzer
{
    private static void ValidateComponent(PrtaBlock block, PrtaSignature? signature, Dictionary<string, PrtaBlock> blockById, List<PrtaDiagnostic> diagnostics)
    {
        // 块体必须为空（10.8）。
        var bodyText = PrtaParser.StripComment(block.Body);
        if (!string.IsNullOrWhiteSpace(bodyText.Replace("\r\n", "\n", StringComparison.Ordinal).Replace('\r', '\n')))
        {
            diagnostics.Add(new PrtaDiagnostic("E_UI", block.Id, 0, "type=\"prtui\" 组件块的块体必须为空。"));
        }

        // 形参表必须为空（11 / 10.8）。
        if (signature is { Params.Count: > 0 })
        {
            diagnostics.Add(new PrtaDiagnostic("E_SIGNATURE", block.Id, 0, "组件块签名的形参表必须为空。"));
        }

        var ui = block.Attributes.TryGetValue("ui", out var uiValue) ? uiValue?.Trim() : null;
        if (ui is null || !ComponentUiKinds.Contains(ui))
        {
            diagnostics.Add(new PrtaDiagnostic("E_UI", block.Id, 0, "组件 ui 属性缺失或非法（button/input/select/checkbox）。"));
            ui = null;
        }

        if (ui == "select" && !block.Attributes.ContainsKey("options"))
        {
            diagnostics.Add(new PrtaDiagnostic("E_UI", block.Id, 0, "select 组件缺少 options 属性。"));
        }

        if (ui is not null && ui != "select" && block.Attributes.ContainsKey("options"))
        {
            diagnostics.Add(new PrtaDiagnostic("E_UI", block.Id, 0, "options 仅用于 select 组件。"));
        }

        // bind 目标格式（@端口 / $变量）；端口须指向更早声明的 prtopt 块。
        if (block.Attributes.TryGetValue("bind", out var bind) && !string.IsNullOrWhiteSpace(bind))
        {
            var t = bind.Trim();
            if (t.StartsWith('@'))
            {
                var portId = t[1..];
                if (!blockById.TryGetValue(portId, out var portBlock)
                    || !string.Equals(portBlock.BlockType, "prtopt", StringComparison.Ordinal)
                    || portBlock.DocumentIndex > block.DocumentIndex)
                {
                    diagnostics.Add(new PrtaDiagnostic("E_UI", block.Id, 0, "bind 的端口目标不存在或声明晚于本组件：@" + portId));
                }
            }
            else if (!t.StartsWith('$'))
            {
                diagnostics.Add(new PrtaDiagnostic("E_UI", block.Id, 0, "bind 目标必须带 @（端口）或 $（变量）前缀：" + t));
            }
        }

        // on 绑定：事件名合法、事件与组件匹配、处理器存在且非组件、处理器形参恰 1 个。
        if (block.Attributes.TryGetValue("on", out var onValue) && !string.IsNullOrWhiteSpace(onValue))
        {
            foreach (var rawPair in onValue.Split(','))
            {
                var pair = rawPair.Trim();
                var colon = pair.IndexOf(':', StringComparison.Ordinal);
                if (colon <= 0 || colon == pair.Length - 1)
                {
                    diagnostics.Add(new PrtaDiagnostic("E_UI", block.Id, 0, "on 绑定书写不合法（应为 事件:处理器id）：" + pair));
                    continue;
                }

                var eventName = pair[..colon].Trim();
                var handlerId = pair[(colon + 1)..].Trim();
                if (!EventNames.Contains(eventName))
                {
                    diagnostics.Add(new PrtaDiagnostic("E_UI", block.Id, 0, "事件名不在集合内（click/change/submit）：" + eventName));
                    continue;
                }

                if (ui is not null && !EventUiMatch[eventName].Split('|').Contains(ui, StringComparer.Ordinal))
                {
                    diagnostics.Add(new PrtaDiagnostic("E_UI", block.Id, 0, $"事件 {eventName} 不适用于组件 {ui}。"));
                }

                if (!blockById.TryGetValue(handlerId, out var handler))
                {
                    diagnostics.Add(new PrtaDiagnostic("E_UI", block.Id, 0, "on 绑定的处理器不存在：" + handlerId));
                    continue;
                }

                if (string.Equals(handler.BlockType, "prtui", StringComparison.Ordinal))
                {
                    diagnostics.Add(new PrtaDiagnostic("E_UI", block.Id, 0, "处理器不得是交互组件块：" + handlerId));
                    continue;
                }

                if (handler.Sig is not null)
                {
                    var handlerSig = PrtaParser.ParseSignature(handler.Sig, diagnostics, handler.Id);
                    if (handlerSig is not null && handlerSig.Params.Count != 1)
                    {
                        diagnostics.Add(new PrtaDiagnostic("E_SIGNATURE", handler.Id, 0, "事件处理器的形参个数必须为 1（事件载荷）。"));
                    }
                }
            }
        }
    }
}
