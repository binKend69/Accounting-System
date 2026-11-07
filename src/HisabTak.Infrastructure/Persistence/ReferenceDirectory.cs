using HisabTak.Application.Security;
using HisabTak.Application.Setup;
using HisabTak.Domain.Common;
using HisabTak.Domain.Security;
using HisabTak.Domain.Setup;
using Microsoft.EntityFrameworkCore;

namespace HisabTak.Infrastructure.Persistence;

internal sealed class ReferenceDirectory(HisabTakDbContext db, IAuthorizationService auth) : IReferenceDirectory
{
    private async Task Demand(int actor, int branch)
    {
        await auth.DemandAsync(actor, PermissionCodes.SetupManage);
        await AccountingAccess.BranchAsync(db, auth, actor, branch, default);
    }

    public async Task<IReadOnlyList<DirectoryRow>> ListAsync(int actor, int branch, DirectoryKind kind)
    {
        await Demand(actor, branch);
        return kind switch
        {
            DirectoryKind.Center => await Rows(db.Centers.IgnoreQueryFilters().Where(x => x.BranchId == branch)),
            DirectoryKind.Department => await Rows(db.Departments.IgnoreQueryFilters()),
            _ => throw new ArgumentOutOfRangeException(nameof(kind))
        };
    }

    private static Task<List<DirectoryRow>> Rows<T>(IQueryable<T> records) where T : ReferenceEntity =>
        records.AsNoTracking().OrderBy(x => x.NameAr).ThenBy(x => x.Id)
            .Select(x => new DirectoryRow(x.Id, x.NameAr, x.IsDeleted, x.RowVersion)).ToListAsync();

    public async Task ApplyAsync(int actor, int branch, DirectoryKind kind, DirectoryAction action, DirectoryRow? original, string name)
    {
        await using var tx = await db.Database.BeginTransactionAsync();
        await DatabaseLock.AcquireAsync(db, "HisabTak.ReferenceDirectory", default);
        await Demand(actor, branch);
        async Task Change<T>(IQueryable<T> records, Func<string,T> create) where T : ReferenceEntity
        {
            T entity;
            if (action is DirectoryAction.Create or DirectoryAction.Rename)
            {
                ArgumentException.ThrowIfNullOrWhiteSpace(name);
                name = name.Trim();
                if (name.Length > 100) throw new ArgumentOutOfRangeException(nameof(name));
                int except = action == DirectoryAction.Rename ? original?.Id ?? 0 : 0;
                if (await records.AnyAsync(x => x.NameAr == name && x.Id != except))
                    throw new DirectoryConflictException("الاسم محجوز لسجل ظاهر أو مخفي.");
            }
            if (action == DirectoryAction.Create)
            {
                entity = create(name);
                db.Add(entity);
            }
            else
            {
                if (original is null) throw new ArgumentException("اختر سجلًا.");
                entity = await records.SingleOrDefaultAsync(x => x.Id == original.Id)
                    ?? throw new DirectoryConflictException("السجل غير متاح.");
                if (!original.Version.SequenceEqual(entity.RowVersion))
                    throw new DirectoryConflictException("تغير السجل. حدّث القائمة قبل المحاولة مجددًا.");
                switch (action)
                {
                    case DirectoryAction.Rename when !entity.IsDeleted: entity.Rename(name); break;
                    case DirectoryAction.Hide when !entity.IsDeleted: entity.MarkDeleted(); break;
                    case DirectoryAction.Restore when entity.IsDeleted: entity.Restore(); break;
                    default: throw new DirectoryConflictException("العملية لا تتوافق مع حالة السجل.");
                }
            }
            await db.SaveChangesAsync();
            db.AuditLogs.Add(new AuditLog(actor, $"REFERENCE_{action.ToString().ToUpperInvariant()}", typeof(T).Name, entity.Id, Environment.MachineName));
        }

        switch (kind)
        {
            case DirectoryKind.Center: await Change(db.Centers.IgnoreQueryFilters().Where(x => x.BranchId == branch), n => new Center(n, branch)); break;
            case DirectoryKind.Department: await Change(db.Departments.IgnoreQueryFilters(), n => new Department(n)); break;
            default: throw new ArgumentOutOfRangeException(nameof(kind));
        }
        await db.SaveChangesAsync();
        await tx.CommitAsync();
    }
}
