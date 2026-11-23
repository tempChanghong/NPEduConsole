using NPEduConsole.Core;

internal static class ClassRuleSourceReviewChecks
{
    public static void Verify()
    {
        var physics = new ClassRuleRemoval("physics", "a1", "实验班", "P1", true, []);
        var retired = physics with { GroupId = "old", GroupCode = "P0", IsActive = false };
        var chemistry = physics with { SubjectId = "chemistry", GroupId = "c1", GroupCode = "C1" };
        var school = new ClassRuleSnapshot("school", new("spring", "春季", "DRAFT"), new("eight", "八班", "8", "grade", true), "base", []);
        var preview = new ClassRulePreview("READY", school, "v1", [], [physics, chemistry, retired], [], [], false, true);
        var review = new ClassRuleSourceReview(preview);
        Check(review.ForSubject("physics").SequenceEqual([physics, retired]), "只显示本科的两条来源，包含停用教学班");
        Check(review.ForSubject("chemistry").SequenceEqual([chemistry]), "同名教学班按科目编号归属");
        review.SetConfirmed("physics", chemistry.GroupId, true);
        Check(review.RemainingCount == 3 && review.ConfirmedGroupIds().Length == 0, "另一科的编号不能代为确认");
        review.SetConfirmed("physics", physics.GroupId, true);
        review.SetConfirmed("physics", physics.GroupId, true);
        Check(review.RemainingFor("physics") == 1 && review.NextUnconfirmed("physics") == retired, "重复勾选后仍准确定位本科待核项");
        review.SetConfirmed("physics", retired.GroupId, true);
        Check(review.NextUnconfirmed("physics") == chemistry && review.RemainingCount == 1, "只勾物理时能找到未勾的化学");
        review.SetConfirmed("chemistry", chemistry.GroupId, true);
        Check(review.NextUnconfirmed() is null && review.ConfirmedGroupIds().SequenceEqual(["a1", "c1", "old"]), "只返回原预览内已确认的实际编号");
        review.SetConfirmed("chemistry", chemistry.GroupId, false);
        Check(review.RemainingCount == 1 && review.IsConfirmed("physics", physics.GroupId), "撤销一次勾选保留其他科目");

        var replacement = physics with { GroupId = "a2", GroupCode = "P2" };
        var updated = new ClassRuleSourceReview(preview with { Version = "v2", Removals = [replacement, chemistry, retired] });
        review.SetConfirmed("chemistry", chemistry.GroupId, true);
        updated.SetConfirmed("physics", "a1", true);
        Check(updated.RemainingCount == 3 && updated.NextUnconfirmed() == replacement && updated.ConfirmedGroupIds().Length == 0,
            "相同数量但不同来源的新预览不会继承勾选或接受旧控件回调");
        Console.WriteLine("PASS: class-rule source review across subjects, repeated checks, inactive links and replaced previews.");
    }

    private static void Check(bool passed, string message)
    {
        if (!passed) throw new InvalidDataException(message);
    }
}
