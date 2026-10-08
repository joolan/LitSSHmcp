namespace LitSSHmcp.App.ViewModels;

/// <summary>预设里的一个模型（名称 + 是否支持视觉）。</summary>
public sealed class PresetModel
{
    public PresetModel(string name, bool vision = false)
    {
        Name = name;
        Vision = vision;
    }

    /// <summary>模型名（OpenAI 兼容 model 参数）。</summary>
    public string Name { get; }

    /// <summary>该模型是否支持图片输入（预设默认值，用户可在设置里逐模型修改）。</summary>
    public bool Vision { get; }

    public override string ToString() => Name;
}

/// <summary>大模型服务商预设：选择后自动填写 Endpoint 与模型列表，用户只需填 API Key。</summary>
public sealed class ModelProviderPreset
{
    public ModelProviderPreset(string name, string endpoint, string defaultModel, IReadOnlyList<PresetModel> models, bool isCustom = false)
    {
        Name = name;
        Endpoint = endpoint;
        DefaultModel = defaultModel;
        Models = models;
        IsCustom = isCustom;
        Type = "openai";
    }

    /// <summary>显示名（下拉里展示）。</summary>
    public string Name { get; }

    /// <summary>接入类型（当前全部为 OpenAI 兼容）。</summary>
    public string Type { get; }

    /// <summary>预设 Endpoint（OpenAI 兼容基地址）。</summary>
    public string Endpoint { get; }

    /// <summary>选择该预设时的默认模型名。</summary>
    public string DefaultModel { get; }

    /// <summary>该服务商的常用模型列表（填充「模型列表」并作为添加模型的候选）。</summary>
    public IReadOnlyList<PresetModel> Models { get; }

    /// <summary>是否为「自定义」（不覆盖 Endpoint/模型，全部手动填写）。</summary>
    public bool IsCustom { get; }

    public override string ToString() => Name;

    /// <summary>自定义（手动填写 Endpoint 与模型）。</summary>
    public static ModelProviderPreset Custom { get; } =
        new("自定义（手动填写）", string.Empty, string.Empty, Array.Empty<PresetModel>(), isCustom: true);

    /// <summary>内置服务商预设。</summary>
    public static IReadOnlyList<ModelProviderPreset> All { get; } = new[]
    {
        new ModelProviderPreset("OpenAI", "https://api.openai.com/v1", "gpt-5-mini",
            new[]
            {
                new PresetModel("gpt-5.2", vision: true),
                new PresetModel("gpt-5.1", vision: true),
                new PresetModel("gpt-5", vision: true),
                new PresetModel("gpt-5-mini", vision: true),
                new PresetModel("gpt-4.1", vision: true),
                new PresetModel("gpt-4.1-mini", vision: true),
                new PresetModel("gpt-4o", vision: true),
                new PresetModel("gpt-4o-mini", vision: true)
            }),

        new ModelProviderPreset("DeepSeek", "https://api.deepseek.com/v1", "deepseek-chat",
            new[]
            {
                new PresetModel("deepseek-chat"),
                new PresetModel("deepseek-reasoner")
            }),

        new ModelProviderPreset("通义千问 Qwen（DashScope）", "https://dashscope.aliyuncs.com/compatible-mode/v1", "qwen-plus",
            new[]
            {
                new PresetModel("qwen3-max"),
                new PresetModel("qwen3-coder-plus"),
                new PresetModel("qwen-max"),
                new PresetModel("qwen-plus"),
                new PresetModel("qwen-turbo"),
                new PresetModel("qwen-omni-turbo", vision: true),
                new PresetModel("qwen-vl-max", vision: true)
            }),

        new ModelProviderPreset("Kimi（月之暗面 Moonshot）", "https://api.moonshot.cn/v1", "kimi-latest",
            new[]
            {
                new PresetModel("kimi-k2-thinking"),
                new PresetModel("kimi-k2-0711-preview"),
                new PresetModel("kimi-latest"),
                new PresetModel("moonshot-v1-32k"),
                new PresetModel("moonshot-v1-128k")
            }),

        new ModelProviderPreset("智谱 GLM（ChatGLM）", "https://open.bigmodel.cn/api/paas/v4", "glm-4.6",
            new[]
            {
                new PresetModel("glm-4.7"),
                new PresetModel("glm-4.6"),
                new PresetModel("glm-4.5"),
                new PresetModel("glm-4.5-air"),
                new PresetModel("glm-4-plus"),
                new PresetModel("glm-4.5v", vision: true),
                new PresetModel("glm-4v-plus", vision: true)
            }),

        new ModelProviderPreset("豆包（火山方舟 Ark）", "https://ark.cn-beijing.volces.com/api/v3", "doubao-seed-1-6-pro",
            new[]
            {
                new PresetModel("doubao-seed-1-6-pro", vision: true),
                new PresetModel("doubao-seed-1-6", vision: true),
                new PresetModel("doubao-1-5-pro-32k-250115"),
                new PresetModel("doubao-1-5-vision-pro-32k-250115", vision: true)
            }),

        new ModelProviderPreset("Gemini（Google AI Studio）", "https://generativelanguage.googleapis.com/v1beta/openai", "gemini-2.5-flash",
            new[]
            {
                new PresetModel("gemini-3-pro-preview", vision: true),
                new PresetModel("gemini-2.5-pro", vision: true),
                new PresetModel("gemini-2.5-flash", vision: true),
                new PresetModel("gemini-2.5-flash-lite", vision: true),
                new PresetModel("gemini-2.0-flash", vision: true)
            }),

        new ModelProviderPreset("Groq", "https://api.groq.com/openai/v1", "llama-3.3-70b-versatile",
            new[]
            {
                new PresetModel("llama-3.3-70b-versatile"),
                new PresetModel("openai/gpt-oss-120b"),
                new PresetModel("qwen3-32b"),
                new PresetModel("llama-4-maverick-17b-128e-instruct", vision: true)
            }),

        new ModelProviderPreset("SiliconFlow（硅基流动）", "https://api.siliconflow.cn/v1", "Qwen/Qwen3-32B",
            new[]
            {
                new PresetModel("Qwen/Qwen3-235B-A22B"),
                new PresetModel("Qwen/Qwen3-32B"),
                new PresetModel("deepseek-ai/DeepSeek-V3.2"),
                new PresetModel("THUDM/GLM-4.6"),
                new PresetModel("Qwen/Qwen2.5-72B-Instruct"),
                new PresetModel("Qwen/Qwen2.5-VL-72B-Instruct", vision: true)
            }),

        new ModelProviderPreset("OpenRouter", "https://openrouter.ai/api/v1", "openai/gpt-5-mini",
            new[]
            {
                new PresetModel("openai/gpt-5.1", vision: true),
                new PresetModel("openai/gpt-5", vision: true),
                new PresetModel("anthropic/claude-sonnet-4.5", vision: true),
                new PresetModel("google/gemini-2.5-flash", vision: true),
                new PresetModel("deepseek/deepseek-chat"),
                new PresetModel("qwen/qwen3-235b-a22b")
            }),

        new ModelProviderPreset("Ollama（本地）", "http://localhost:11434/v1", "qwen3",
            new[]
            {
                new PresetModel("qwen3"),
                new PresetModel("llama3.1"),
                new PresetModel("deepseek-r1"),
                new PresetModel("gemma3", vision: true),
                new PresetModel("llava", vision: true)
            }),

        Custom
    };

    /// <summary>按 Endpoint 找到匹配的预设（找不到返回「自定义」）。</summary>
    public static ModelProviderPreset FindByEndpoint(string? endpoint)
    {
        var key = (endpoint ?? string.Empty).Trim().TrimEnd('/');
        if (key.Length == 0)
            return Custom;
        foreach (var p in All)
        {
            if (!p.IsCustom && string.Equals(p.Endpoint.TrimEnd('/'), key, StringComparison.OrdinalIgnoreCase))
                return p;
        }
        return Custom;
    }
}
