using System.Globalization;
using System.IO;
using System.Text;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Documents;
using System.Windows.Media;
using System.Windows.Shell;
using Perisc.Safety;
using Prt.App.Models;
using Prt.App.Rendering;
using Prt.App.Views;
using Prt.Core;
using Prt.Core.Diagnostics;
using Prt.Core.Export;
using Prt.Core.Rendering;
using Prt.Core.Syntax;

namespace Prt.App.Services;

/// <summary>自检用例组：教程课程 / 教程窗口 / 设置窗口 / 练习 / 结业测试（<see cref="SelfTest"/> 的 partial）。</summary>
internal static partial class SelfTest
{
    /// <summary>自检用例组：教程课程 / 教程窗口 / 设置窗口 / 练习 / 结业测试。</summary>
    private static void RunTutorialCases(CaseRunner runner)
    {
        // 16. 教程课程内容：每课示例都要能被当前解析器接受（故意留错用于教学的那一课除外）。
        //     教程里写"照抄就能用"的示例若报了错，等于教错人——这条守住它。
        runner.Check("教程各课示例均可解析", () => TutorialCase16());

        // 17. 教程窗口可实例化并逐课切换（覆盖 XAML 装配错误与课程索引越界）。
        runner.Check("教程窗口可逐课打开", () => TutorialCase17());

        // 18. 设置窗口可用：构造 → 逐页真实布局 → 逐控件模拟操作 → 把草稿应用回主窗口。
        //     覆盖「设置界面不可使用」这类只能靠肉眼发现的故障：控件零尺寸、事件没接上、
        //     预览回调抛异常、草稿没反映改动，任意一项都会在这里红掉。
        runner.Check("设置窗口可打开且各项可交互", () => TutorialCase18());

        // 19. 教程练习：每课的参考答案必须判定通过、起始片段必须至少有一项不通过。
        //     这两条一起保证「练习是能做的、也是有难度的」——只写数据不写判定就会退化成摆设。
        runner.Check("教程练习的答案与判定自洽", () => TutorialCase19());

        // 20. 结业测试：题库结构合法 + 批改算法正确（满分 / 零分 / 恰好通过 / 差一题），
        //     并跑一遍窗口的答题→交卷链路（覆盖界面构建与反馈渲染）。
        runner.Check("结业测试题库与批改", () => TutorialCase20());
    }

    /// <summary>用例 16：教程各课示例均可解析</summary>
    private static string TutorialCase16()
    {
            var options = new PrtOptions();
            var failures = new List<string>();
            var intentional = 0;

            foreach (var lesson in TutorialLessons.All)
            {
                var document = PrtParser.Parse(lesson.Snippet, options);
                var errors = document.SortedDiagnostics.Count(d => d.Severity == DiagnosticSeverity.Error);

                if (lesson.IntentionalDiagnostic)
                {
                    if (document.SortedDiagnostics.Count == 0)
                    {
                        failures.Add(lesson.TitleZh + "：本应产生诊断，实际一条都没有");
                    }
                    else
                    {
                        intentional++;
                    }

                    continue;
                }

                if (errors > 0)
                {
                    var first = document.SortedDiagnostics.First(d => d.Severity == DiagnosticSeverity.Error);
                    failures.Add($"{lesson.TitleZh}：{errors} 个错误（{first.Format()}）");
                }
            }

            if (failures.Count > 0)
            {
                throw new InvalidOperationException(string.Join("；", failures));
            }

            return $"{TutorialLessons.All.Count} 课示例均被接受（{TutorialLessons.All.Count - intentional} 课零错误，"
                   + $"{intentional} 课故意留错用于教学）";
    }

    /// <summary>用例 17：教程窗口可逐课打开</summary>
    private static string TutorialCase17()
    {
            var window = new TutorialWindow();
            if (window.LessonCount < 3)
            {
                throw new InvalidOperationException($"课程数过少：{window.LessonCount}");
            }

            for (var i = 0; i < window.LessonCount; i++)
            {
                window.ShowLesson(i);
            }

            window.ShowLesson(window.LessonCount + 5);   // 越界应被忽略，不抛异常
            window.ShowLesson(-1);

            if (window.CurrentLessonIndex != window.LessonCount - 1)
            {
                throw new InvalidOperationException("越界索引不应改变当前课");
            }

            return $"共 {window.LessonCount} 课，逐课切换正常，越界索引被忽略";
    }

    /// <summary>用例 18：设置窗口可打开且各项可交互</summary>
    private static string TutorialCase18()
    {
            var window = new MainWindow();
            var dialog = new SettingsWindow();
            dialog.LoadFrom(window.CaptureSettingsForTest());

            var pushed = new List<AppSettings>();
            dialog.SettingsChanged += (_, draft) => pushed.Add(draft);

            var root = (FrameworkElement)dialog.Content
                       ?? throw new InvalidOperationException("设置窗口没有内容");

            window.ApplySettingsForTest(dialog.Draft);

            // TabControl 只实例化当前页，必须逐页切过去布局才能发现「某一页控件零尺寸」。
            var pages = new (string Title, FrameworkElement[] Controls)[]
            {
                ("外观", [dialog.InterfaceBox, dialog.PreviewThemeBox, dialog.ZoomSlider]),
                ("编辑", [dialog.WordWrapBox, dialog.StrictBox, dialog.ViewModeBox]),
                ("启动与语言", [dialog.SplashBox, dialog.LanguageBox]),
            };

            if (dialog.MainTabs.Items.Count != pages.Length)
            {
                throw new InvalidOperationException(
                    $"设置页签数量异常：{dialog.MainTabs.Items.Count}（预期 {pages.Length}）");
            }

            for (var i = 0; i < pages.Length; i++)
            {
                dialog.MainTabs.SelectedIndex = i;
                root.Measure(new Size(628, 540));
                root.Arrange(new Rect(0, 0, 628, 540));
                root.UpdateLayout();

                foreach (var element in pages[i].Controls)
                {
                    if (element.ActualWidth <= 1 || element.ActualHeight <= 1)
                    {
                        throw new InvalidOperationException(
                            $"「{pages[i].Title}」页的控件未完成布局：{element.ActualWidth:0}×{element.ActualHeight:0}");
                    }
                }
            }

            dialog.MainTabs.SelectedIndex = 0;
            root.UpdateLayout();

            // 启动图预览区：自定义图与「内置图」示意二者必须有且只有一个在显示。
            var customShown = dialog.SplashPreview.Visibility == Visibility.Visible;
            var builtinShown = dialog.SplashBuiltinPreview.Visibility == Visibility.Visible;
            if (customShown == builtinShown)
            {
                throw new InvalidOperationException(
                    $"启动图预览状态异常：自定义 {dialog.SplashPreview.Visibility} / 内置 {dialog.SplashBuiltinPreview.Visibility}");
            }

            // 逐项改成与当前不同的值（保证真的触发变更事件）。
            var wantDark = !dialog.Draft.InterfaceDark;
            dialog.InterfaceBox.SelectedIndex = wantDark ? 1 : 0;
            dialog.PreviewThemeBox.SelectedIndex = dialog.PreviewThemeBox.SelectedIndex == 1 ? 2 : 1;
            dialog.ZoomSlider.Value = Math.Abs(dialog.ZoomSlider.Value - 150d) < 0.5d ? 160d : 150d;
            dialog.ViewModeBox.SelectedIndex = dialog.ViewModeBox.SelectedIndex == 0 ? 1 : 0;
            dialog.SplashBox.IsChecked = !(dialog.SplashBox.IsChecked == true);
            dialog.SplashBox.RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent));
            dialog.StrictBox.IsChecked = !(dialog.StrictBox.IsChecked == true);
            dialog.StrictBox.RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent));
            dialog.LanguageBox.SelectedIndex = dialog.LanguageBox.SelectedIndex == 1 ? 2 : 1;

            if (pushed.Count < 6)
            {
                throw new InvalidOperationException($"选项变更未推送草稿：仅 {pushed.Count} 次");
            }

            if (dialog.Draft.InterfaceDark != wantDark)
            {
                throw new InvalidOperationException("界面配色的改动没有收进草稿");
            }

            if (Math.Abs(dialog.Draft.Zoom - dialog.ZoomSlider.Value / 100d) > 0.001d)
            {
                throw new InvalidOperationException($"缩放没有收进草稿：滑块 {dialog.ZoomSlider.Value:0} / 草稿 {dialog.Draft.Zoom * 100:0}");
            }

            if ((dialog.Draft.DefaultViewMode == AppSettings.ViewModeEditor) != (dialog.ViewModeBox.SelectedIndex == 0))
            {
                throw new InvalidOperationException("默认显示方式没有收进草稿");
            }

            // 每次预览都走主窗口的同一条应用路径：这里会真的换配色、换视图模式、设缩放与折行，
            // 任何一步抛异常都说明设置页一旦操作就会把主界面弄坏。
            window.ApplySettingsForTest(pushed[^1]);
            window.ApplySettingsForTest(window.CaptureSettingsForTest());
            root.UpdateLayout();

            return $"3 页均布局正常，{pushed.Count} 项改动均即时预览，回应用主窗口无异常";
    }

    /// <summary>用例 19：教程练习的答案与判定自洽</summary>
    private static string TutorialCase19()
    {
            var problems = new List<string>();
            var withExercise = 0;

            for (var i = 0; i < TutorialLessons.All.Count; i++)
            {
                var exercise = TutorialExercises.For(i);
                if (exercise is null)
                {
                    continue;
                }

                withExercise++;
                var title = TutorialLessons.All[i].TitleZh;

                var answer = exercise.Evaluate(exercise.AnswerSnippet);
                if (!answer.AllPassed)
                {
                    var failing = answer.Checks.Where(c => !c.Passed).Select(c => c.Description);
                    problems.Add($"{title}：参考答案未通过（{string.Join("、", failing)}）");
                }

                if (exercise.Evaluate(exercise.StartSnippet).AllPassed)
                {
                    problems.Add($"{title}：起始片段已经全部通过，练习没有难度");
                }

                if (exercise.Requirements.Count < 2)
                {
                    problems.Add($"{title}：判定要求少于 2 条");
                }
            }

            if (withExercise < TutorialLessons.All.Count - 1)
            {
                problems.Add($"带练习的课过少：{withExercise} / {TutorialLessons.All.Count}");
            }

            if (problems.Count > 0)
            {
                throw new InvalidOperationException(string.Join("；", problems));
            }

            return $"{withExercise} 课有练习：参考答案全部通过、起始片段均未通过";
    }

    /// <summary>用例 20：结业测试题库与批改</summary>
    private static string TutorialCase20()
    {
            var problems = new List<string>();
            var prompts = new HashSet<string>(StringComparer.Ordinal);

            foreach (var question in TutorialQuiz.All)
            {
                if (question.Options.Count < 2)
                {
                    problems.Add("选项少于 2 个：" + question.PromptZh);
                }

                if (question.CorrectIndex < 0 || question.CorrectIndex >= question.Options.Count)
                {
                    problems.Add("正确答案越界：" + question.PromptZh);
                }

                if (string.IsNullOrWhiteSpace(question.WhyZh) || string.IsNullOrWhiteSpace(question.WhyEn))
                {
                    problems.Add("缺少解析：" + question.PromptZh);
                }

                if (!prompts.Add(question.PromptZh))
                {
                    problems.Add("题干重复：" + question.PromptZh);
                }
            }

            if (problems.Count > 0)
            {
                throw new InvalidOperationException(string.Join("；", problems));
            }

            var total = TutorialQuiz.All.Count;
            var all = TutorialQuiz.All.Select(q => q.CorrectIndex).ToList();

            var full = TutorialQuiz.Grade(all);
            if (!full.Passed || full.Score != 100 || full.Correct != total)
            {
                throw new InvalidOperationException($"全对应满分通过：得分 {full.Score}、通过 {full.Passed}");
            }

            var empty = TutorialQuiz.Grade(Enumerable.Repeat(-1, total).ToList());
            if (empty.Passed || empty.Score != 0 || empty.Correct != 0)
            {
                throw new InvalidOperationException($"未作答应 0 分不通过：得分 {empty.Score}");
            }

            List<int> WithFirstCorrect(int correctCount) => TutorialQuiz.All
                .Select((q, i) => i < correctCount ? q.CorrectIndex : (q.CorrectIndex + 1) % q.Options.Count)
                .ToList();

            var borderline = TutorialQuiz.Grade(WithFirstCorrect(TutorialQuiz.PassCount));
            if (!borderline.Passed || borderline.Correct != TutorialQuiz.PassCount)
            {
                throw new InvalidOperationException("恰好答对通过线题数应当通过");
            }

            if (TutorialQuiz.Grade(WithFirstCorrect(TutorialQuiz.PassCount - 1)).Passed)
            {
                throw new InvalidOperationException("比通过线少答对一题不应通过");
            }

            // 窗口链路：题目都构建出来了，且答题 → 交卷不会抛异常。
            var window = new TutorialWindow();
            if (window.QuizQuestionCount != total)
            {
                throw new InvalidOperationException($"测验界面题数 {window.QuizQuestionCount}，题库 {total}");
            }

            var answers = window.CollectAnswers();
            if (answers.Count != total || answers.Any(a => a != -1))
            {
                throw new InvalidOperationException("未作答时应全部为 -1");
            }

            var graded = window.SubmitQuiz(all);
            window.ResetQuiz();
            if (window.CollectAnswers().Any(a => a != -1))
            {
                throw new InvalidOperationException("重做一遍后应清空作答");
            }

            return $"题库 {total} 题，通过线 {TutorialQuiz.PassCount} 题；满分 / 零分 / 临界批改与界面链路均正确（窗口判分 {graded.Score}）";
    }
}
