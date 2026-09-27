using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Media;
using Prt.App.Rendering;
using Prt.App.Services;
using Prt.App.Theming;
using Prt.Core;
using Prt.Core.Rendering;

namespace Prt.App.Views;

/// <summary>
/// 互动教程窗口：左边选课，右边两个页签。
/// <list type="bullet">
/// <item>「课程与练习」：本课说明 + 示例 + 效果预览 + 可校验的动手练习；</item>
/// <item>「结业测试」：覆盖各课的单选测验，交卷即判分。</item>
/// </list>
/// <para>
/// 与「打开一份教程文档」的区别：这里是**教学型**的——有可试的示例、可交的练习、
/// 可考的测验，而且效果预览用的是编辑器同一套渲染管线（解析 → <see cref="PreviewBuilder"/>），
/// 所见即所得，不是预先写死的截图。
/// </para>
/// <para>
/// 刻意**不含 PRTA（受限计算）内容**：本编辑器只声明实现 CORE + EXT，不支持 COMP。
/// </para>
/// </summary>
public partial class TutorialWindow : Window
{
    private readonly Func<string, bool>? _insertIntoEditor;
    private readonly Func<string?>? _readEditor;

    /// <summary>每题一组单选钮（下标与题库一致）。</summary>
    private readonly List<RadioButton[]> _quizRadios = [];

    /// <summary>每题的批改反馈文本（交卷前隐藏）。</summary>
    private readonly List<TextBlock> _quizFeedback = [];

    /// <summary>最后一题的提示（未作答统计用）。</summary>
    private int _index;

    /// <param name="insertIntoEditor">
    /// 把一段文本插入编辑器的回调（由主窗口提供，插到当前光标处）；返回 false 表示当前没有可插入的文档。
    /// </param>
    /// <param name="readEditor">
    /// 读取当前编辑器全文的回调，供练习「校验我的写法」使用；返回 null 表示没有打开的文档。
    /// </param>
    public TutorialWindow(Func<string, bool>? insertIntoEditor = null, Func<string?>? readEditor = null)
    {
        InitializeComponent();

        _insertIntoEditor = insertIntoEditor;
        _readEditor = readEditor;

        LessonList.ItemsSource = TutorialLessons.All.Select(lesson => lesson.Title).ToList();
        BuildQuizPanel();

        if (LessonList.Items.Count > 0)
        {
            LessonList.SelectedIndex = 0;
        }
    }

    /// <summary>课程总数（自检用）。</summary>
    public int LessonCount => TutorialLessons.All.Count;

    /// <summary>当前课序号（0 起，自检用）。</summary>
    public int CurrentLessonIndex => _index;

    /// <summary>结业测试题数（自检用）。</summary>
    internal int QuizQuestionCount => _quizRadios.Count;

    protected override void OnSourceInitialized(EventArgs e)
    {
        base.OnSourceInitialized(e);
        DarkTitleBar.Apply(this);
    }

    // ─────────────────────────────── 课程 ───────────────────────────────

    /// <summary>显示第 <paramref name="index"/> 课（0 起）。</summary>
    internal void ShowLesson(int index)
    {
        var lessons = TutorialLessons.All;
        if (index < 0 || index >= lessons.Count)
        {
            return;
        }

        _index = index;
        var lesson = lessons[index];

        LessonTitle.Text = lesson.Title;
        LessonBody.Text = lesson.Body;
        SnippetBox.Text = lesson.Snippet;
        ProgressText.Text = Localizer.T("tutorial.progress", index + 1, lessons.Count);
        MessageText.Text = string.Empty;

        DemoView.Document = BuildDemo(lesson.Snippet);
        ShowPractice(TutorialExercises.For(index), index);

        PrevLessonButton.IsEnabled = index > 0;
        NextLessonButton.IsEnabled = index < lessons.Count - 1;
    }

    /// <summary>
    /// 用编辑器同一条渲染管线渲染本课示例。
    /// 解析 / 渲染失败时不留空白，而是把失败原因就地显示出来（教学场景下这本身也是信息）。
    /// </summary>
    private FlowDocument BuildDemo(string snippet)
    {
        try
        {
            var document = PrtParser.Parse(snippet, new PrtOptions());

            // 演示跟随界面配色：深色界面下用 dark 主题，否则 default。
            var theme = PrtTheme.Resolve(App.Palette.IsDark ? "dark" : "default", document.ThemeBlocks, out _);
            var style = new PreviewStyle(theme);
            var builder = new PreviewBuilder(style);
            var flow = builder.Build(document).Document;

            DemoView.Background = style.PageBackground;
            return flow;
        }
        catch (Exception ex)
        {
            DemoView.Background = null;
            return new FlowDocument(new Paragraph(new Run("示例渲染失败：" + ex.Message)));
        }
    }

    // ─────────────────────────────── 练习 ───────────────────────────────

    /// <summary>切换练习面板：没有练习的课（如第 13 课）整块隐藏。</summary>
    private void ShowPractice(TutorialExercise? exercise, int lessonIndex)
    {
        if (exercise is null)
        {
            PracticePanel.Visibility = Visibility.Collapsed;
            return;
        }

        PracticePanel.Visibility = Visibility.Visible;
        PracticePrompt.Text = exercise.Prompt;
        PracticeResultText.Text = string.Empty;
        PracticeAnswerBox.Visibility = Visibility.Collapsed;
        PracticeNoteLabel.Visibility = Visibility.Collapsed;

        var done = SettingsStore.Current.TutorialPracticeDone.Contains(lessonIndex);
        PracticeDoneText.Text = done ? Localizer.T("tutorial.practice.done") : string.Empty;
    }

    /// <summary>判定一份文本（界面与自检共用同一路径）。</summary>
    internal ExerciseResult EvaluatePractice(string text)
        => TutorialExercises.For(_index)?.Evaluate(text)
           ?? new ExerciseResult([]);

    private void OnPracticeInsertClick(object sender, RoutedEventArgs e)
    {
        var exercise = TutorialExercises.For(_index);
        if (exercise is null)
        {
            return;
        }

        if (_insertIntoEditor is null || !_insertIntoEditor(exercise.StartSnippet))
        {
            PracticeResultText.Text = Localizer.T("tutorial.no.editor");
            return;
        }

        PracticeResultText.Text = Localizer.T("tutorial.inserted");
        Owner?.Activate();
    }

    private void OnPracticeCheckClick(object sender, RoutedEventArgs e)
    {
        var text = _readEditor?.Invoke();
        if (string.IsNullOrEmpty(text))
        {
            PracticeResultText.Text = Localizer.T("tutorial.practice.noeditor");
            return;
        }

        var result = EvaluatePractice(text);
        var lines = result.Checks.Select(c => (c.Passed ? "✓ " : "✗ ") + c.Description);
        var head = result.AllPassed
            ? Localizer.T("tutorial.practice.passed")
            : Localizer.T("tutorial.practice.failed", result.Total - result.Passed);

        PracticeResultText.Text = head + "\n" + string.Join("\n", lines);

        if (result.AllPassed)
        {
            PracticeDoneText.Text = Localizer.T("tutorial.practice.done");
            MarkPracticeDone(_index);
        }
    }

    /// <summary>把一课记为「练习完成」并落盘（重复完成不会重复写）。</summary>
    private static void MarkPracticeDone(int lessonIndex)
    {
        if (!SettingsStore.Current.TutorialPracticeDone.Contains(lessonIndex))
        {
            SettingsStore.Current.TutorialPracticeDone.Add(lessonIndex);
            // 教程进度不是关键数据：写不进去不打断学习流程
            // （「程序目录不可写」的整体提示由设置路径负责，见 MainWindow.PersistUiSettings）。
            SettingsStore.TrySave(out _);
        }
    }

    private void OnPracticeAnswerClick(object sender, RoutedEventArgs e)
    {
        var exercise = TutorialExercises.For(_index);
        if (exercise is null)
        {
            return;
        }

        if (PracticeAnswerBox.Visibility == Visibility.Visible)
        {
            PracticeAnswerBox.Visibility = Visibility.Collapsed;
            PracticeNoteLabel.Visibility = Visibility.Collapsed;
            return;
        }

        PracticeAnswerText.Text = exercise.AnswerSnippet;
        PracticeNoteLabel.Visibility = Visibility.Visible;
        PracticeAnswerBox.Visibility = Visibility.Visible;
        PracticeResultText.Text = Localizer.T("tutorial.practice.note") + "：" + exercise.Note;
    }

    // ─────────────────────────────── 结业测试 ───────────────────────────────

    /// <summary>按题库构建试卷（每题一组单选钮 + 一块批改反馈）。</summary>
    private void BuildQuizPanel()
    {
        QuizSubtitleText.Text = Localizer.T("tutorial.quiz.subtitle", TutorialQuiz.PassCount);

        for (var i = 0; i < TutorialQuiz.All.Count; i++)
        {
            var question = TutorialQuiz.All[i];

            var block = new StackPanel { Margin = new Thickness(0, 0, 0, 18) };
            block.Children.Add(new TextBlock
            {
                Text = $"{i + 1}. {question.Prompt}",
                TextWrapping = TextWrapping.Wrap,
                FontWeight = FontWeights.SemiBold,
            });

            var radios = new RadioButton[question.Options.Count];
            for (var o = 0; o < question.Options.Count; o++)
            {
                var radio = new RadioButton
                {
                    Content = question.Options[o],
                    GroupName = "quiz-" + i,
                    Margin = new Thickness(18, 6, 0, 0),
                };

                radios[o] = radio;
                block.Children.Add(radio);
            }

            var feedback = new TextBlock
            {
                Margin = new Thickness(18, 6, 0, 0),
                TextWrapping = TextWrapping.Wrap,
                Visibility = Visibility.Collapsed,
            };

            block.Children.Add(feedback);
            QuizPanel.Children.Add(block);

            _quizRadios.Add(radios);
            _quizFeedback.Add(feedback);
        }

        UpdateBestScore(null);
    }

    /// <summary>收集当前作答（未选为 -1），供交卷与自检使用。</summary>
    internal IReadOnlyList<int> CollectAnswers()
        => _quizRadios
            .Select(group => Array.FindIndex(group, radio => radio.IsChecked == true))
            .ToList();

    /// <summary>交卷：批改、显示逐题反馈、刷新最好成绩。</summary>
    internal QuizResult SubmitQuiz(IReadOnlyList<int> answers)
    {
        var result = TutorialQuiz.Grade(answers);

        for (var i = 0; i < result.Items.Count && i < _quizFeedback.Count; i++)
        {
            var item = result.Items[i];
            var feedback = _quizFeedback[i];
            var mark = item.Unanswered
                ? Localizer.T("tutorial.quiz.unanswered")
                : item.Correct
                    ? Localizer.T("tutorial.quiz.right")
                    : Localizer.T("tutorial.quiz.wrong", item.Question.Options[item.Question.CorrectIndex]);

            feedback.Text = mark + "\n" + Localizer.T("tutorial.quiz.why") + "：" + item.Question.Why;
            feedback.Foreground = item.Correct
                ? (Brush)FindResource("App.Accent")
                : (Brush)FindResource("App.Text");
            feedback.Visibility = Visibility.Visible;
        }

        QuizResultText.Text =
            Localizer.T("tutorial.quiz.score", result.Score, result.Correct, result.Total) + "\n" +
            (result.Passed ? Localizer.T("tutorial.quiz.passed") : Localizer.T("tutorial.quiz.failed"));

        UpdateBestScore(result);
        return result;
    }

    private void UpdateBestScore(QuizResult? result)
    {
        if (result is { } r && r.Score > SettingsStore.Current.TutorialBestScore)
        {
            SettingsStore.Current.TutorialBestScore = r.Score;
            SettingsStore.TrySave(out _);
        }

        var best = SettingsStore.Current.TutorialBestScore;
        BestScoreText.Text = best >= 0 ? Localizer.T("tutorial.quiz.best", best) : string.Empty;
    }

    /// <summary>清空作答与反馈（重做一遍）。</summary>
    internal void ResetQuiz()
    {
        foreach (var radios in _quizRadios)
        {
            foreach (var radio in radios)
            {
                radio.IsChecked = false;
            }
        }

        foreach (var feedback in _quizFeedback)
        {
            feedback.Visibility = Visibility.Collapsed;
            feedback.Text = string.Empty;
        }

        QuizResultText.Text = string.Empty;
    }

    // ─────────────────────────────── 事件 ───────────────────────────────

    private void OnLessonSelected(object sender, SelectionChangedEventArgs e)
    {
        if (IsInitialized)
        {
            ShowLesson(LessonList.SelectedIndex);
        }
    }

    private void OnPrevLessonClick(object sender, RoutedEventArgs e)
    {
        if (_index > 0)
        {
            LessonList.SelectedIndex = _index - 1;
        }
    }

    private void OnNextLessonClick(object sender, RoutedEventArgs e)
    {
        if (_index < TutorialLessons.All.Count - 1)
        {
            LessonList.SelectedIndex = _index + 1;
        }
    }

    private void OnInsertClick(object sender, RoutedEventArgs e)
    {
        if (_insertIntoEditor is null || !_insertIntoEditor(SnippetBox.Text))
        {
            MessageText.Text = Localizer.T("tutorial.no.editor");
            return;
        }

        MessageText.Text = Localizer.T("tutorial.inserted");

        // 把焦点交回主窗口：插入后通常想立刻在编辑器里看效果。
        Owner?.Activate();
    }

    private void OnSubmitQuizClick(object sender, RoutedEventArgs e) => SubmitQuiz(CollectAnswers());

    private void OnRetakeQuizClick(object sender, RoutedEventArgs e) => ResetQuiz();
}
