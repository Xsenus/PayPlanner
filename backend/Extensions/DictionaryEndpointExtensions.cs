using PayPlanner.Api.Data;
using System.Reflection;

namespace PayPlanner.Api.Extensions
{
    public static class DictionaryEndpointExtensions
    {
        /// <summary>
        /// Универсальный CRUD для сущности-справочника.
        /// </summary>
        public static RouteGroupBuilder MapDictionaryCrud<T>(
            this RouteGroupBuilder parent,
            string routeSegment,
            Func<T, T, T> applyUpdate)
            where T : class
        {
            var group = parent.MapGroup(routeSegment);

            // CREATE
            group.MapPost(string.Empty, async (PaymentContext db, T model) =>
            {
                db.Set<T>().Add(model);
                await db.SaveChangesAsync();

                // Берём Id через reflection (Id int)
                var id = model?.GetType().GetProperty("Id", BindingFlags.Public | BindingFlags.Instance)?.GetValue(model);
                return Results.Created($"/api/dictionaries{routeSegment}/{id}", model);
            });

            // UPDATE
            group.MapPut("/{id:int}", async (PaymentContext db, int id, T model) =>
            {
                var entity = await db.Set<T>().FindAsync(id);
                if (entity is null) return Results.NotFound();

                applyUpdate(entity, model);
                await db.SaveChangesAsync();
                return Results.Ok(entity);
            });

            // DELETE
            group.MapDelete("/{id:int}", async (PaymentContext db, int id) =>
            {
                var entity = await db.Set<T>().FindAsync(id);
                if (entity is null) return Results.NotFound();

                db.Set<T>().Remove(entity);
                await db.SaveChangesAsync();
                return Results.NoContent();
            });

            return group;
        }

        /// <summary>
        /// Универсальный toggle для поля IsActive (bool) через reflection.
        /// </summary>
        public static RouteGroupBuilder MapToggleActive<T>(this RouteGroupBuilder group)
            where T : class
        {
            group.MapPatch("/{id:int}/toggle-active", async (PaymentContext db, int id) =>
            {
                var entity = await db.Set<T>().FindAsync(id);
                if (entity is null) return Results.NotFound();

                var prop = typeof(T).GetProperty("IsActive", BindingFlags.Public | BindingFlags.Instance);
                if (prop is null || prop.PropertyType != typeof(bool))
                    return Results.BadRequest("Сущность не поддерживает переключение активности (нет bool IsActive).");

                var current = (bool)(prop.GetValue(entity) ?? false);
                prop.SetValue(entity, !current);

                await db.SaveChangesAsync();
                return Results.Ok(new { Id = id, IsActive = !current });
            });

            return group;
        }
    }
}
