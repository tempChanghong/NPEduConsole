export function nextStudyMonday() {
  const calendar = new Date(Date.now() + 8 * 60 * 60 * 1000);
  calendar.setUTCDate(calendar.getUTCDate() + ((8 - calendar.getUTCDay()) % 7 || 7));
  return calendar.toISOString().slice(0, 10);
}

export async function seedClassStudy(prisma, { school, term, manager, grade, classes, date }) {
  const after = days => new Date(Date.parse(date + 'T00:00:00Z') + 86400000 * days).toISOString().slice(0, 10);
  const rule = (days, start, end) => ({ days, start, end });
  const calendar = (rules, exceptions = [], effectiveTo = null) => ({ timeZone: 'Asia/Shanghai',
    effectiveFrom: date, effectiveTo, rules, exceptions });
  const gradePolicy = { mode: 'Override', settings: {
    schedule: calendar([rule([1, 2], '18:00', '18:30'), rule([2], '19:00', '19:30'), rule([3, 4], '20:00', '20:45')],
      [{ date: after(2), kind: 'Skip', weekday: null }, { date: after(4), kind: 'UseWeekday', weekday: 4 }]),
    examSchedule: calendar([rule([1, 6], '13:00', '13:45')], [{ date: after(5), kind: 'Skip', weekday: null }]),
    presentation: { noiseDisplay: 'StatusDbfs', temporaryHideSeconds: 120 },
    homework: { enabled: true, sourceMode: 'OwnClass', sourceClassIds: [], rotationSeconds: 82 },
    noise: { enabled: true, redLineDbfs: -34, mediumLineDbfs: -49, disciplineCooldownSeconds: 710, highContinuousSeconds: 4,
      estimated: { highDbA: 69, mediumDbA: 51 } },
    photos: { enabled: true, continuousSeconds: 4, cooldownSeconds: 45, retentionDays: 28, captureOnExit: true,
      extendedRetentionAcknowledged: false },
  } };
  const classPolicy = structuredClone(gradePolicy);
  classPolicy.settings.schedule = calendar([rule([1], '18:15', '18:55'), rule([4], '18:30', '19:30')],
    [{ date: after(8), kind: 'Skip', weekday: null }], after(28));
  for (const [kind, id, policy] of [['GRADE', grade.id, gradePolicy], ['CLASS', classes[1], classPolicy]]) {
    await prisma.$executeRaw`INSERT INTO "NpepStudySetting" ("schoolId","termId","targetType","targetId",revision,policy,"updatedBy","updatedAt")
      VALUES (${school.id},${term.id},${kind},${id},1,${JSON.stringify(policy)}::jsonb,${manager.id},NOW())`;
  }
  const legacy = { mode: 'Override', rules: [rule([1], '18:00', '19:00')] };
  await prisma.$executeRaw`INSERT INTO "NpepNoiseSchedulePolicy" ("schoolId","termId","targetType","targetId",revision,policy,"updatedBy","updatedAt")
    VALUES (${school.id},${term.id},'CLASS',${classes[3]},1,${JSON.stringify(legacy)}::jsonb,${manager.id},NOW())`;
}
