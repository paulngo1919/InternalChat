using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace InternalChat.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class SearchIndex : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            ArgumentNullException.ThrowIfNull(migrationBuilder);

            // Entirely hand-written: EF Core cannot express a STORED generated column, a GIN index,
            // or a function definition, and `message` is a partitioned table it does not create
            // either. See SearchIndexSql for why the immutable unaccent wrapper is needed and what
            // promise it makes.
            migrationBuilder.Sql(SearchIndexSql.CreateImmutableUnaccent);
            migrationBuilder.Sql(SearchIndexSql.AddSearchColumn);
            migrationBuilder.Sql(SearchIndexSql.AddAttachmentNameIndex);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            ArgumentNullException.ThrowIfNull(migrationBuilder);

            migrationBuilder.Sql(SearchIndexSql.Drop);
        }
    }
}
