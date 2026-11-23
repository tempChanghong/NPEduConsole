using System.Text.Json;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Threading;
using Avalonia.VisualTree;
using NPEduConsole.App;
using NPEduConsole.Core;

internal static partial class Program
{
    private sealed record ScopedTermAccess(string Origin, string SchoolId, string SchoolCode, string Username, string Password,
        string AutumnId, string SpringId, string ArchiveId, string SpringRoomId, string ArchiveRoomId, string StoppedId,
        string SpringGradeId, string OtherSchoolId, bool AllowScopedTermWrites);

    private static int StartScopedTermClasses(string path, string output)
    {
        var access = JsonSerializer.Deserialize<ScopedTermAccess>(File.ReadAllText(path), JsonSerializerOptions.Web)
            ?? throw new InvalidDataException("Missing isolated scoped term fixture.");
        Check(access.AllowScopedTermWrites && IsLoopback(access.Origin), "Scoped term writes require the isolated fixture.");
        Directory.CreateDirectory(output);
        return AppBuilder.Configure<ConsoleApplication>().UsePlatformDetect()
            .AfterSetup(builder => Dispatcher.UIThread.Post(() => _ = VerifyScopedTermClassesAsync(access, output)))
            .StartWithClassicDesktopLifetime([]);
    }

    private static async Task VerifyScopedTermClassesAsync(ScopedTermAccess access, string output)
    {
        var desktop = (IClassicDesktopStyleApplicationLifetime)Application.Current!.ApplicationLifetime!;
        var main = (MainWindow)desktop.MainWindow!;
        using var control = new HttpClient { BaseAddress = new Uri(access.Origin), Timeout = TimeSpan.FromSeconds(65) };
        try
        {
            main.Width = 1160;
            main.Height = 780;
            Find<TextBox>(main, "ServerAddress").Text = access.Origin;
            Find<TextBox>(main, "SchoolCode").Text = access.SchoolCode;
            Find<TextBox>(main, "Username").Text = access.Username;
            Find<TextBox>(main, "Password").Text = access.Password;
            Click(Find<Button>(main, "LoginButton"));
            var schools = Find<ComboBox>(main, "SchoolPicker");
            await Until(() => schools.Items.OfType<School>().Any(school => school.Id == access.OtherSchoolId), "真实可选两所学校");
            schools.SelectedItem = schools.Items.OfType<School>().Single(school => school.Id == access.SchoolId);
            Click(Find<Button>(main, "OpenClassWorkspaceButton"));
            await Step("before");
            await PressScoped(main, "OpenTermPreparationButton");
            var terms = await AcademicPageAsync<TermPreparationView>(main);
            var termList = Find<ListBox>(terms, "PreparedTerms");
            await Until(() => termList.Items.OfType<ManagedAcademicTerm>().Count() == 3, "三学期目录");
            SelectTerm(access.SpringId);
            await PressScoped(terms, "TermManageClasses");
            var structure = await Show<TermClassManagementWindow>();
            structure.Width = 1160;
            structure.Height = 780;
            await Until(() => Read(structure, "StructureActiveTerm").Contains("秋季学期") &&
                Read(structure, "StructureTarget").Contains("草稿"), "春季草稿与秋季启用同时显示");
            var grade = Find<ComboBox>(structure, "StructureGrade");
            await Until(() => grade.ItemCount == 2, "草稿年级");
            grade.SelectedIndex = 1;
            await PressScoped(structure, "StructureGradeEdit");
            var gradeEditor = await Show<TermGradeEditWindow>();
            Find<TextBox>(gradeEditor, "GradeNameInput").Text = "高二 · 春季";
            Find<TextBox>(gradeEditor, "GradeCodeInput").Text = "SG2";
            Find<TextBox>(gradeEditor, "GradeOrderInput").Text = "25";
            await Capture(gradeEditor, output, "01-draft-grade-edit");
            await PressScoped(gradeEditor, "GradeEditSave");
            await Until(() => Read(gradeEditor, "GradeEditNotice").Contains("输入已锁定"), "改年级丢回包且首次核对失败");
            var frozenGradeName = Find<TextBox>(gradeEditor, "GradeNameInput");
            Check(frozenGradeName.IsReadOnly && frozenGradeName.Text == "高二 · 春季" &&
                !Find<Button>(gradeEditor, "GradeEditSave").IsEnabled, "核对前无法把已提交的 A 改成另一份 B");
            await PressScoped(gradeEditor, "GradeCurrentCheck");
            await Until(() => Read(gradeEditor, "GradeEditNotice").Contains("服务器当前值与本次提交一致"), "只读查到 A");
            await PressScoped(gradeEditor, "GradeEditClose");
            await Until(() => !gradeEditor.IsVisible && grade.SelectedItem?.ToString()?.Contains("SG2") == true, "刷新且保留原年级 ID");
            var list = Find<TermClassDirectoryView>(structure, "StructureDirectory");
            list.Select(access.SpringRoomId);
            await Until(() => list.Selected?.Id == access.SpringRoomId && Read(structure, "StructureRosterSummary") == "学生名单为空",
                "草稿七班按 ID 选中");
            await Capture(structure, output, "02-wide-draft-and-current");

            await PressScoped(structure, "StructureCreateClass");
            var batch = await Show<ClassCreationWindow>();
            await Until(() => (Find<ComboBox>(batch, "CreationGrade").SelectedItem as ClassCreationGrade)?.Id == access.SpringGradeId,
                "批量开班自动带入选中年级");
            await PressScoped(batch, "OpenClassBatchButton");
            Find<TextBox>(batch, "BatchEndNumber").Text = "2";
            await PressScoped(batch, "BatchPreviewButton");
            await Until(() => Find<ItemsControl>(batch, "BatchRows").ItemCount == 2, "草稿两班");
            await PressScoped(batch, "BatchSubmitButton");
            await Until(() => Find<ListBox>(batch, "BatchResults").ItemCount == 2, "服务端返回同学期两班 ID");
            await Capture(batch, output, "03-draft-batch-receipt");
            await PressScoped(batch, "BatchCloseButton");
            await Until(() => !batch.IsVisible && Find<Button>(structure, "StructureCreateClass").IsEffectivelyEnabled, "批量返回");
            await PressScoped(structure, "StructureCreateClass");
            var single = await Show<ClassCreationWindow>();
            Find<TextBox>(single, "CreationName").Text = "春季单建示例班";
            Find<TextBox>(single, "CreationCode").Text = "SPRING-ONE";
            await PressScoped(single, "SubmitClassCreationButton");
            await Until(() => Read(single, "CreationFeedback").Contains("先只读核对"), "首次回包模拟丢失");
            await PressScoped(single, "SubmitClassCreationButton");
            var singleRoster = await Show<ClassRosterWindow>();
            await Until(() => Read(singleRoster, "RosterScope").Contains("春季单建示例班"), "同一编号找回真实班 ID，并打开原班名单");
            await PressScoped(singleRoster, "CloseRosterButton");
            await Until(() => !singleRoster.IsVisible && Find<Button>(structure, "StructureReload").IsEffectivelyEnabled, "建班结果回目录");

            structure.Width = 840;
            structure.Height = 480;
            await PressScoped(structure, "StructureBackToClasses");
            await Until(() => list.IsEffectivelyVisible && list.IsEffectivelyEnabled && list.Selected?.Id != access.SpringRoomId,
                "窄窗回到原回执班后再选择");
            list.Select(access.SpringRoomId);
            await Until(() => list.Selected?.Id == access.SpringRoomId, "窄窗回到刚选班级");
            await Capture(structure, output, "04-compact-class-list");
            await PressScoped(structure, "StructureOpenClass");
            await Until(() => Find<Button>(structure, "StructureRoster").IsFocused, "打开班级后焦点落在名单");
            await PressScoped(structure, "StructureRoster");
            var springRoster = await Show<ClassRosterWindow>();
            springRoster.Width = 840;
            springRoster.Height = 480;
            await Until(() => Find<Grid>(springRoster, "RosterEditorPane").IsVisible, "春季名单编辑");
            Find<TextBox>(springRoster, "RosterPasteText").Text = "001\t春季原同学\n002\t春季新增同学";
            await PressScoped(springRoster, "ApplyRosterImport");
            await PressScoped(springRoster, "PreviewRosterButton");
            await Until(() => Find<ListBox>(springRoster, "RosterChangesList").ItemCount == 2, "两份有效名单差异");
            await Capture(springRoster, output, "05-compact-roster-difference");
            await PressScoped(springRoster, "ConfirmRosterSave");
            await Until(() => Read(springRoster, "RosterFeedback").Contains("已保存「春季学期"), "春季写入名单");
            await EditName(springRoster, "春季原同学", "桌面未保存草稿");
            await PressScoped(springRoster, "PreviewRosterButton");
            await Step("legacy-writes");
            await PressScoped(springRoster, "ConfirmRosterSave");
            await Until(() => Find<ListBox>(springRoster, "CurrentRosterRows").ItemCount == 2 &&
                Read(springRoster, "RosterFeedback").Contains("核对两份名单"), "旧网页抢先写，桌面读回学校当前名单");
            Check(Rows(springRoster).Any(box => box.Text == "桌面未保存草稿"), "冲突后桌面仍保留原输入");
            await Step("conflict-zero-overwrite");
            await Capture(springRoster, output, "06-compact-legacy-conflict");
            springRoster.Close();
            await PressScoped(springRoster, "ConfirmRosterDiscard");
            await Until(() => !springRoster.IsVisible && Find<Button>(structure, "StructureRoster").IsFocused, "关闭冲突稿返回同班和原焦点");
            await PressScoped(structure, "StructureBackToClasses");
            list.Select(access.StoppedId);
            await PressScoped(structure, "StructureOpenClass");
            await PressScoped(structure, "StructureRoster");
            var stopped = await Show<ClassRosterWindow>();
            await Until(() => Read(stopped, "RosterFeedback").Contains("仅供查看"), "停用班名单只读");
            Check(!Find<Button>(stopped, "PreviewRosterButton").IsVisible && !Find<Button>(stopped, "AddRosterStudent").IsVisible,
                "停用班未提供预览或新增");
            await PressScoped(stopped, "CloseRosterButton");
            await PressScoped(structure, "StructureEditClass");
            var restore = await Show<ClassMaintenanceWindow>();
            await PressScoped(restore, "ReactivateClassButton");
            await Until(() => !restore.IsVisible && list.Selected is { IsActive: true } selected && selected.Id == access.StoppedId,
                "指定草稿内恢复原班 ID");
            await PressScoped(structure, "StructureEditClass");
            var deactivate = await Show<ClassMaintenanceWindow>();
            await PressScoped(deactivate, "ReadDeactivationImpactButton");
            await Until(() => Find<Button>(deactivate, "ConfirmDeactivateButton").IsEffectivelyEnabled &&
                Find<Button>(deactivate, "ConfirmDeactivateButton").IsEffectivelyVisible, "从真实关联读取停用影响");
            await Capture(deactivate, output, "06b-draft-deactivation-impact");
            await PressScoped(deactivate, "ConfirmDeactivateButton");
            await Until(() => !deactivate.IsVisible && list.Selected is { IsActive: false } selected && selected.Id == access.StoppedId,
                "停用保留相同 ID");
            await PressScoped(structure, "StructureClose");
            await Until(() => !structure.IsVisible && Find<Button>(terms, "TermManageClasses").IsEffectivelyEnabled,
                "返回学期设置并完成列表刷新");

            SelectTerm(access.ArchiveId);
            await PressScoped(terms, "TermManageClasses");
            var archived = await Show<TermClassManagementWindow>();
            archived.Width = 1160;
            archived.Height = 780;
            var archivedList = Find<TermClassDirectoryView>(archived, "StructureDirectory");
            await Until(() => Read(archived, "StructureTarget").Contains("已归档"), "归档范围清楚显示");
            archivedList.Select(access.ArchiveRoomId);
            await Until(() => Find<ListBox>(archived, "StructureRosterRows").ItemCount == 1, "归档默认显示只读名单");
            Check((string?)Find<Button>(archived, "StructureRoster").Content == "更正名单", "只有显式更正才进编辑");
            await Capture(archived, output, "07-wide-archived-read");
            await Step("legacy-rename-before-roster");
            await PressScoped(archived, "StructureRoster");
            var archiveRoster = await Show<ClassRosterWindow>();
            await Until(() => Read(archiveRoster, "RosterScope").Contains("归档一版"), "旧目录打开名单时使用服务器当前班名");
            await EditName(archiveRoster, "归档原姓名", "归档已更正");
            await PressScoped(archiveRoster, "PreviewRosterButton");
            await Step("legacy-rename-after-preview");
            await PressScoped(archiveRoster, "ConfirmRosterSave");
            await Until(() => Read(archiveRoster, "RosterFeedback").Contains("归档二版"), "版本冲突时核对到该班的新名称");
            Check(Rows(archiveRoster).Any(box => box.Text == "归档已更正") &&
                !Find<Button>(archiveRoster, "AdoptRosterRevisionButton").IsEnabled, "保留草稿，班级身份变化要求返回");
            archiveRoster.Close();
            await PressScoped(archiveRoster, "ConfirmRosterDiscard");
            await PressScoped(archived, "StructureRoster");
            archiveRoster = await Show<ClassRosterWindow>();
            await Until(() => Read(archiveRoster, "RosterScope").Contains("归档二版"), "重开编辑器显示最新班名");
            await EditName(archiveRoster, "归档原姓名", "归档已更正");
            await PressScoped(archiveRoster, "PreviewRosterButton");
            await PressScoped(archiveRoster, "ConfirmRosterSave");
            await Until(() => Read(archiveRoster, "RosterFeedback").Contains("已保存「归档学期"), "归档有效班保存");
            await Capture(archiveRoster, output, "08-archived-correction");
            await PressScoped(archiveRoster, "CloseRosterButton");
            await PressScoped(archived, "StructureClose");
            await Step("archive-and-draft");
            await Until(() => !archived.IsVisible && Find<Button>(terms, "TermManageClasses").IsEffectivelyEnabled,
                "归档校订返回后刷新学期目录");

            SelectTerm(access.SpringId);
            await PressScoped(terms, "TermStatusActivate");
            var activation = await Show<TermStatusWindow>();
            await PressScoped(activation, "StatusCommit");
            await Until(() => Read(activation, "StatusAfterLabel") == "服务器确认的结果", "正常启用草稿");
            await PressScoped(activation, "StatusBack");
            await Until(() => !activation.IsVisible, "关闭启用结果");
            await Step("active-spring");
            await PressScoped(terms, "TermBack");
            var classes = Find<ListBox>(main, "ClassPicker");
            await Until(() => classes.Items.OfType<Classroom>().Any(room => room.Id == access.SpringRoomId), "日常班级目录读回原春季班 ID");
            classes.SelectedItem = classes.Items.OfType<Classroom>().Single(room => room.Id == access.SpringRoomId);
            await PressScoped(main, "OpenClassRosterButton");
            var nowCurrent = await Show<ClassRosterWindow>();
            await Until(() => Rows(nowCurrent).Any(box => box.Text == "旧网页先保存") &&
                Rows(nowCurrent).Any(box => box.Text == "001"), "日常入口读回原名单与前导零");
            await Capture(nowCurrent, output, "09-spring-roster-from-normal-entry");
            await PressScoped(nowCurrent, "CloseRosterButton");
            schools.SelectedItem = schools.Items.OfType<School>().Single(school => school.Id == access.OtherSchoolId);
            await Until(() => classes.Items.OfType<Classroom>().All(room => room.Id != access.SpringRoomId), "切校清除旧班位置");
            Console.WriteLine("PASS: Windows Console、Edge、KV/PostgreSQL 三学期同名班，草稿批量与单建回执、旧网页名单冲突、归档 ID/考勤及启用后日常名单。");
            Environment.ExitCode = 0;

            void SelectTerm(string id) => termList.SelectedItem = termList.Items.OfType<ManagedAcademicTerm>().Single(term => term.Id == id);
            async Task Step(string name)
            {
                using var response = await control.PostAsync("/fixture/scoped/" + name, null);
                if (!response.IsSuccessStatusCode) throw new InvalidOperationException(name + " → " + await response.Content.ReadAsStringAsync());
            }
            async Task<T> Show<T>() where T : Window
            {
                await Until(() => desktop.Windows.OfType<T>().Any(window => window.IsVisible), $"打开 {typeof(T).Name}");
                return desktop.Windows.OfType<T>().Single(window => window.IsVisible);
            }
        }
        catch (Exception error)
        {
            Console.Error.WriteLine(error);
            if (desktop.Windows.LastOrDefault(window => window.IsVisible) is { } window)
                await Capture(window, output, "scoped-term-failure");
            Environment.ExitCode = 1;
        }
        finally { desktop.Shutdown(Environment.ExitCode); }
    }

    private static string Read(Control host, string name) => Find<TextBlock>(host, name).Text ?? "";
    private static async Task PressScoped(Control window, string name)
    {
        var button = Find<Button>(window, name);
        await Until(() => button.IsEffectivelyVisible && button.IsEffectivelyEnabled, name);
        button.BringIntoView();
        Click(button);
    }

    private static IEnumerable<TextBox> Rows(ClassRosterWindow window) =>
        Find<ItemsControl>(window, "RosterRows").GetVisualDescendants().OfType<TextBox>();

    private static async Task EditName(ClassRosterWindow window, string before, string after)
    {
        await Until(() => Rows(window).Any(box => box.Text == before), before);
        Rows(window).Single(box => box.Text == before).Text = after;
    }
}
