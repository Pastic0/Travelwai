using Npgsql;

namespace TravelwAI.Data.Services;

public sealed class SupabaseSchemaInitializer
{
    private const string ProtectedAdminEmail = "2324802010387@student.tdmu.edu.vn";
    private readonly NpgsqlDataSource _dataSource;

    public SupabaseSchemaInitializer(NpgsqlDataSource dataSource)
    {
        _dataSource = dataSource;
    }

    public async Task EnsureCreatedAsync()
    {
        await using var conn = await _dataSource.OpenConnectionAsync();
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = $$"""
            create table if not exists app_documents (
                collection text not null,
                id text not null,
                data jsonb not null default '{}'::jsonb,
                created_at timestamptz not null default now(),
                updated_at timestamptz not null default now(),
                primary key (collection, id)
            );

            create index if not exists ix_app_documents_collection
                on app_documents(collection);

            create index if not exists ix_app_documents_updated_at
                on app_documents(collection, updated_at desc);

            create index if not exists ix_app_documents_data_gin
                on app_documents using gin(data jsonb_path_ops);

            create index if not exists ix_app_documents_heritage_chunks_source_id
                on app_documents((data ->> 'source_id'))
                where collection = 'heritage_chunks';

            create index if not exists ix_app_documents_heritage_chunks_status
                on app_documents((data ->> 'approval_status'))
                where collection = 'heritage_chunks';

            create index if not exists ix_app_documents_heritage_sources_status
                on app_documents((data ->> 'approval_status'))
                where collection = 'heritage_sources';

            create table if not exists app_users_auth (
                id text primary key,
                email text not null unique,
                username text not null,
                password_hash text not null,
                password_salt text not null,
                refresh_token_hash text,
                refresh_token_expires_at timestamptz,
                created_at timestamptz not null default now(),
                updated_at timestamptz not null default now(),
                last_login_at timestamptz
            );

            alter table app_users_auth
                add column if not exists role text not null default 'Free';

            alter table app_users_auth
                alter column role set default 'Free';

            alter table app_users_auth
                add column if not exists is_locked boolean not null default false;

            alter table app_users_auth
                add column if not exists is_protected boolean not null default false;

            alter table app_users_auth
                add column if not exists tour_sales_level integer not null default 1;

            create index if not exists ix_app_users_auth_email
                on app_users_auth(lower(email));

            create index if not exists ix_app_users_auth_refresh_token_hash
                on app_users_auth(refresh_token_hash);

            create index if not exists ix_app_users_auth_role
                on app_users_auth(role);

            update app_users_auth
            set role = 'Free', updated_at = now()
            where role = 'User';

            update app_users_auth
            set role = 'Business', updated_at = now()
            where role = 'Company';

            update app_documents
            set data = jsonb_set(data, '{role}', to_jsonb('Free'::text), true),
                updated_at = now()
            where collection = 'users' and data->>'role' = 'User';

            update app_documents
            set data = jsonb_set(data, '{role}', to_jsonb('Business'::text), true),
                updated_at = now()
            where collection = 'users' and data->>'role' = 'Company';

            create table if not exists password_reset_codes (
                id text primary key,
                email text not null,
                code_hash text not null,
                reset_token_hash text,
                expires_at timestamptz not null,
                verified_at timestamptz,
                used_at timestamptz,
                created_at timestamptz not null default now(),
                updated_at timestamptz not null default now()
            );

            create index if not exists ix_password_reset_codes_email
                on password_reset_codes(lower(email));

            create index if not exists ix_password_reset_codes_code_hash
                on password_reset_codes(code_hash);

            create index if not exists ix_password_reset_codes_reset_token_hash
                on password_reset_codes(reset_token_hash);

            update app_users_auth
            set role = 'Free',
                is_protected = false,
                updated_at = now()
            where id in (
                select id
                from (
                    select id
                    from app_users_auth
                    where lower(email) <> '{{ProtectedAdminEmail}}'
                      and role = 'Admin'
                    order by created_at asc
                    offset 3
                ) overflow_admins
            );

            insert into app_users_auth(
                id,
                email,
                username,
                password_hash,
                password_salt,
                role,
                is_locked,
                is_protected,
                created_at,
                updated_at,
                last_login_at
            )
            values (
                'admin2324802010387',
                '{{ProtectedAdminEmail}}',
                'Admin TravelwAI',
                'vvpjagLb3YXLPWLe7GEt+Gp/FPahKQEZIOoyBGpbBOg=',
                'DJQ92z0coezou3ZsM8+ZrnfzmPOBCSkwXRmogVPYNjU=',
                'Admin',
                false,
                true,
                now(),
                now(),
                now()
            )
            on conflict (email) do update
            set id = excluded.id,
                username = excluded.username,
                password_hash = excluded.password_hash,
                password_salt = excluded.password_salt,
                role = 'Admin',
                is_locked = false,
                is_protected = true,
                updated_at = now();

            insert into app_documents(collection, id, data, created_at, updated_at)
            values (
                'users',
                'admin2324802010387',
                jsonb_build_object(
                    'id', 'admin2324802010387',
                    'uid', 'admin2324802010387',
                    'email', '{{ProtectedAdminEmail}}',
                    'username', 'Admin TravelwAI',
                    'displayName', 'Admin TravelwAI',
                    'role', 'Admin',
                    'is_locked', false,
                    'is_protected', true,
                    'is_active', true,
                    'created_at', now(),
                    'createdAt', now(),
                    'registeredAt', now(),
                    'updated_at', now()
                ),
                now(),
                now()
            )
            on conflict (collection, id) do update
            set data = app_documents.data || excluded.data,
                updated_at = now();

            -- Tự động thêm 20 tour mẫu TravelwAI. Nếu database đã có 3 tour cũ,
            -- đoạn này chỉ bổ sung các tour còn thiếu và không ghi đè tour người dùng đã sửa.
            insert into app_documents(collection, id, data, created_at, updated_at)
            values
                    (
                        'tours',
                        'tour-da-lat-3n2d',
                        jsonb_build_object(
                            'id', 'tour-da-lat-3n2d',
                            'name', 'Tour Đà Lạt 3 ngày 2 đêm',
                            'destination', 'Đà Lạt',
                            'description', 'Khám phá Langbiang, chợ đêm, hồ Tuyền Lâm và các điểm check-in nổi bật.',
                            'price', 2490000,
                            'duration', '3 ngày 2 đêm',
                            'start_date', to_char(current_date + interval '7 days', 'YYYY-MM-DD'),
                            'end_date', to_char(current_date + interval '9 days', 'YYYY-MM-DD'),
                            'slots', 20,
                            'sold', 0,
                            'status', 'Đang bán',
                            'image', '/main_site_image/back1.png',
                            'created_at', now(),
                            'updated_at', now()
                        ),
                        now(),
                        now()
                    ),
                    (
                        'tours',
                        'tour-da-nang-hoi-an-4n3d',
                        jsonb_build_object(
                            'id', 'tour-da-nang-hoi-an-4n3d',
                            'name', 'Tour Đà Nẵng - Hội An 4 ngày 3 đêm',
                            'destination', 'Đà Nẵng, Hội An',
                            'description', 'Bà Nà Hills, biển Mỹ Khê, phố cổ Hội An và ẩm thực miền Trung.',
                            'price', 3890000,
                            'duration', '4 ngày 3 đêm',
                            'start_date', to_char(current_date + interval '14 days', 'YYYY-MM-DD'),
                            'end_date', to_char(current_date + interval '17 days', 'YYYY-MM-DD'),
                            'slots', 25,
                            'sold', 0,
                            'status', 'Đang bán',
                            'image', '/main_site_image/back2.png',
                            'created_at', now(),
                            'updated_at', now()
                        ),
                        now(),
                        now()
                    ),
                    (
                        'tours',
                        'tour-phu-quoc-3n2d',
                        jsonb_build_object(
                            'id', 'tour-phu-quoc-3n2d',
                            'name', 'Tour Phú Quốc 3 ngày 2 đêm',
                            'destination', 'Phú Quốc',
                            'description', 'Nghỉ dưỡng biển đảo, tham quan Nam đảo, chợ đêm và trải nghiệm địa phương.',
                            'price', 3290000,
                            'duration', '3 ngày 2 đêm',
                            'start_date', to_char(current_date + interval '21 days', 'YYYY-MM-DD'),
                            'end_date', to_char(current_date + interval '23 days', 'YYYY-MM-DD'),
                            'slots', 18,
                            'sold', 0,
                            'status', 'Đang bán',
                            'image', '/main_site_image/back3.png',
                            'created_at', now(),
                            'updated_at', now()
                        ),
                        now(),
                        now()
                    ),
                    (
                        'tours',
                        'tour-ha-noi-ninh-binh-3n2d',
                        jsonb_build_object(
                            'id', 'tour-ha-noi-ninh-binh-3n2d',
                            'name', 'Tour Hà Nội - Ninh Bình 3 ngày 2 đêm',
                            'destination', 'Hà Nội, Ninh Bình',
                            'description', 'Tham quan phố cổ Hà Nội, Tràng An, Tam Cốc, Hang Múa và ẩm thực miền Bắc.',
                            'price', 2990000,
                            'duration', '3 ngày 2 đêm',
                            'start_date', to_char(current_date + interval '10 days', 'YYYY-MM-DD'),
                            'end_date', to_char(current_date + interval '12 days', 'YYYY-MM-DD'),
                            'slots', 24,
                            'sold', 0,
                            'status', 'Đang bán',
                            'image', '/main_site_image/back4.png',
                            'created_at', now(),
                            'updated_at', now()
                        ),
                        now(),
                        now()
                    ),
                    (
                        'tours',
                        'tour-ha-long-yen-tu-3n2d',
                        jsonb_build_object(
                            'id', 'tour-ha-long-yen-tu-3n2d',
                            'name', 'Tour Hạ Long - Yên Tử 3 ngày 2 đêm',
                            'destination', 'Quảng Ninh',
                            'description', 'Du thuyền vịnh Hạ Long, khám phá hang động, check-in biển đảo và hành hương Yên Tử.',
                            'price', 3590000,
                            'duration', '3 ngày 2 đêm',
                            'start_date', to_char(current_date + interval '12 days', 'YYYY-MM-DD'),
                            'end_date', to_char(current_date + interval '14 days', 'YYYY-MM-DD'),
                            'slots', 22,
                            'sold', 0,
                            'status', 'Đang bán',
                            'image', '/main_site_image/back5.png',
                            'created_at', now(),
                            'updated_at', now()
                        ),
                        now(),
                        now()
                    ),
                    (
                        'tours',
                        'tour-sapa-fansipan-3n2d',
                        jsonb_build_object(
                            'id', 'tour-sapa-fansipan-3n2d',
                            'name', 'Tour Sa Pa - Fansipan 3 ngày 2 đêm',
                            'destination', 'Lào Cai, Sa Pa',
                            'description', 'Săn mây Sa Pa, bản Cát Cát, đỉnh Fansipan và không khí núi rừng Tây Bắc.',
                            'price', 3190000,
                            'duration', '3 ngày 2 đêm',
                            'start_date', to_char(current_date + interval '16 days', 'YYYY-MM-DD'),
                            'end_date', to_char(current_date + interval '18 days', 'YYYY-MM-DD'),
                            'slots', 20,
                            'sold', 0,
                            'status', 'Đang bán',
                            'image', '/main_site_image/back1.png',
                            'created_at', now(),
                            'updated_at', now()
                        ),
                        now(),
                        now()
                    ),
                    (
                        'tours',
                        'tour-ha-giang-dong-van-4n3d',
                        jsonb_build_object(
                            'id', 'tour-ha-giang-dong-van-4n3d',
                            'name', 'Tour Hà Giang - Đồng Văn 4 ngày 3 đêm',
                            'destination', 'Hà Giang',
                            'description', 'Cung đường đèo Mã Pí Lèng, cao nguyên đá Đồng Văn, sông Nho Quế và văn hóa bản địa.',
                            'price', 4590000,
                            'duration', '4 ngày 3 đêm',
                            'start_date', to_char(current_date + interval '18 days', 'YYYY-MM-DD'),
                            'end_date', to_char(current_date + interval '21 days', 'YYYY-MM-DD'),
                            'slots', 18,
                            'sold', 0,
                            'status', 'Đang bán',
                            'image', '/main_site_image/back2.png',
                            'created_at', now(),
                            'updated_at', now()
                        ),
                        now(),
                        now()
                    ),
                    (
                        'tours',
                        'tour-cao-bang-ban-gioc-3n2d',
                        jsonb_build_object(
                            'id', 'tour-cao-bang-ban-gioc-3n2d',
                            'name', 'Tour Cao Bằng - Bản Giốc 3 ngày 2 đêm',
                            'destination', 'Cao Bằng',
                            'description', 'Khám phá thác Bản Giốc, động Ngườm Ngao, núi Mắt Thần và cảnh sắc biên giới.',
                            'price', 3390000,
                            'duration', '3 ngày 2 đêm',
                            'start_date', to_char(current_date + interval '20 days', 'YYYY-MM-DD'),
                            'end_date', to_char(current_date + interval '22 days', 'YYYY-MM-DD'),
                            'slots', 20,
                            'sold', 0,
                            'status', 'Đang bán',
                            'image', '/main_site_image/back3.png',
                            'created_at', now(),
                            'updated_at', now()
                        ),
                        now(),
                        now()
                    ),
                    (
                        'tours',
                        'tour-moc-chau-ta-xua-3n2d',
                        jsonb_build_object(
                            'id', 'tour-moc-chau-ta-xua-3n2d',
                            'name', 'Tour Mộc Châu - Tà Xùa 3 ngày 2 đêm',
                            'destination', 'Sơn La',
                            'description', 'Đồi chè Mộc Châu, thung lũng hoa, săn mây Tà Xùa và trải nghiệm vùng cao.',
                            'price', 2890000,
                            'duration', '3 ngày 2 đêm',
                            'start_date', to_char(current_date + interval '22 days', 'YYYY-MM-DD'),
                            'end_date', to_char(current_date + interval '24 days', 'YYYY-MM-DD'),
                            'slots', 24,
                            'sold', 0,
                            'status', 'Đang bán',
                            'image', '/main_site_image/back4.png',
                            'created_at', now(),
                            'updated_at', now()
                        ),
                        now(),
                        now()
                    ),
                    (
                        'tours',
                        'tour-hue-di-san-2n1d',
                        jsonb_build_object(
                            'id', 'tour-hue-di-san-2n1d',
                            'name', 'Tour Huế di sản 2 ngày 1 đêm',
                            'destination', 'Huế',
                            'description', 'Đại Nội Huế, lăng tẩm, chùa Thiên Mụ, sông Hương và ẩm thực cố đô.',
                            'price', 1990000,
                            'duration', '2 ngày 1 đêm',
                            'start_date', to_char(current_date + interval '24 days', 'YYYY-MM-DD'),
                            'end_date', to_char(current_date + interval '25 days', 'YYYY-MM-DD'),
                            'slots', 28,
                            'sold', 0,
                            'status', 'Đang bán',
                            'image', '/main_site_image/back5.png',
                            'created_at', now(),
                            'updated_at', now()
                        ),
                        now(),
                        now()
                    ),
                    (
                        'tours',
                        'tour-nha-trang-bien-dao-3n2d',
                        jsonb_build_object(
                            'id', 'tour-nha-trang-bien-dao-3n2d',
                            'name', 'Tour Nha Trang biển đảo 3 ngày 2 đêm',
                            'destination', 'Nha Trang',
                            'description', 'Tắm biển, tham quan đảo, VinWonders, chợ Đầm và thưởng thức hải sản địa phương.',
                            'price', 3490000,
                            'duration', '3 ngày 2 đêm',
                            'start_date', to_char(current_date + interval '26 days', 'YYYY-MM-DD'),
                            'end_date', to_char(current_date + interval '28 days', 'YYYY-MM-DD'),
                            'slots', 26,
                            'sold', 0,
                            'status', 'Đang bán',
                            'image', '/main_site_image/back1.png',
                            'created_at', now(),
                            'updated_at', now()
                        ),
                        now(),
                        now()
                    ),
                    (
                        'tours',
                        'tour-quy-nhon-phu-yen-4n3d',
                        jsonb_build_object(
                            'id', 'tour-quy-nhon-phu-yen-4n3d',
                            'name', 'Tour Quy Nhơn - Phú Yên 4 ngày 3 đêm',
                            'destination', 'Quy Nhơn, Phú Yên',
                            'description', 'Kỳ Co, Eo Gió, Ghềnh Đá Đĩa, Bãi Xép và cung biển miền Trung.',
                            'price', 3990000,
                            'duration', '4 ngày 3 đêm',
                            'start_date', to_char(current_date + interval '28 days', 'YYYY-MM-DD'),
                            'end_date', to_char(current_date + interval '31 days', 'YYYY-MM-DD'),
                            'slots', 22,
                            'sold', 0,
                            'status', 'Đang bán',
                            'image', '/main_site_image/back2.png',
                            'created_at', now(),
                            'updated_at', now()
                        ),
                        now(),
                        now()
                    ),
                    (
                        'tours',
                        'tour-mui-ne-phan-thiet-2n1d',
                        jsonb_build_object(
                            'id', 'tour-mui-ne-phan-thiet-2n1d',
                            'name', 'Tour Mũi Né - Phan Thiết 2 ngày 1 đêm',
                            'destination', 'Bình Thuận',
                            'description', 'Đồi cát bay, làng chài Mũi Né, Bàu Trắng, biển xanh và đặc sản Phan Thiết.',
                            'price', 1890000,
                            'duration', '2 ngày 1 đêm',
                            'start_date', to_char(current_date + interval '30 days', 'YYYY-MM-DD'),
                            'end_date', to_char(current_date + interval '31 days', 'YYYY-MM-DD'),
                            'slots', 30,
                            'sold', 0,
                            'status', 'Đang bán',
                            'image', '/main_site_image/back3.png',
                            'created_at', now(),
                            'updated_at', now()
                        ),
                        now(),
                        now()
                    ),
                    (
                        'tours',
                        'tour-tphcm-cu-chi-1n',
                        jsonb_build_object(
                            'id', 'tour-tphcm-cu-chi-1n',
                            'name', 'Tour TP.HCM - Củ Chi 1 ngày',
                            'destination', 'TP.HCM, Củ Chi',
                            'description', 'Dinh Độc Lập, Nhà thờ Đức Bà, Bưu điện Thành phố và địa đạo Củ Chi.',
                            'price', 890000,
                            'duration', '1 ngày',
                            'start_date', to_char(current_date + interval '32 days', 'YYYY-MM-DD'),
                            'end_date', to_char(current_date + interval '32 days', 'YYYY-MM-DD'),
                            'slots', 35,
                            'sold', 0,
                            'status', 'Đang bán',
                            'image', '/main_site_image/back4.png',
                            'created_at', now(),
                            'updated_at', now()
                        ),
                        now(),
                        now()
                    ),
                    (
                        'tours',
                        'tour-can-tho-my-tho-3n2d',
                        jsonb_build_object(
                            'id', 'tour-can-tho-my-tho-3n2d',
                            'name', 'Tour Cần Thơ - Mỹ Tho 3 ngày 2 đêm',
                            'destination', 'Cần Thơ, Tiền Giang',
                            'description', 'Chợ nổi Cái Răng, miệt vườn, cù lao Thới Sơn và văn hóa sông nước miền Tây.',
                            'price', 2790000,
                            'duration', '3 ngày 2 đêm',
                            'start_date', to_char(current_date + interval '34 days', 'YYYY-MM-DD'),
                            'end_date', to_char(current_date + interval '36 days', 'YYYY-MM-DD'),
                            'slots', 26,
                            'sold', 0,
                            'status', 'Đang bán',
                            'image', '/main_site_image/back5.png',
                            'created_at', now(),
                            'updated_at', now()
                        ),
                        now(),
                        now()
                    ),
                    (
                        'tours',
                        'tour-con-dao-3n2d',
                        jsonb_build_object(
                            'id', 'tour-con-dao-3n2d',
                            'name', 'Tour Côn Đảo 3 ngày 2 đêm',
                            'destination', 'Bà Rịa - Vũng Tàu, Côn Đảo',
                            'description', 'Biển xanh Côn Đảo, nghĩa trang Hàng Dương, miếu bà Phi Yến và hành trình tâm linh.',
                            'price', 4990000,
                            'duration', '3 ngày 2 đêm',
                            'start_date', to_char(current_date + interval '36 days', 'YYYY-MM-DD'),
                            'end_date', to_char(current_date + interval '38 days', 'YYYY-MM-DD'),
                            'slots', 18,
                            'sold', 0,
                            'status', 'Đang bán',
                            'image', '/main_site_image/back1.png',
                            'created_at', now(),
                            'updated_at', now()
                        ),
                        now(),
                        now()
                    ),
                    (
                        'tours',
                        'tour-tay-nguyen-buon-ma-thuot-3n2d',
                        jsonb_build_object(
                            'id', 'tour-tay-nguyen-buon-ma-thuot-3n2d',
                            'name', 'Tour Tây Nguyên - Buôn Ma Thuột 3 ngày 2 đêm',
                            'destination', 'Đắk Lắk',
                            'description', 'Bảo tàng cà phê, Buôn Đôn, hồ Lắk, thác Dray Nur và văn hóa cồng chiêng.',
                            'price', 3190000,
                            'duration', '3 ngày 2 đêm',
                            'start_date', to_char(current_date + interval '38 days', 'YYYY-MM-DD'),
                            'end_date', to_char(current_date + interval '40 days', 'YYYY-MM-DD'),
                            'slots', 22,
                            'sold', 0,
                            'status', 'Đang bán',
                            'image', '/main_site_image/back2.png',
                            'created_at', now(),
                            'updated_at', now()
                        ),
                        now(),
                        now()
                    ),
                    (
                        'tours',
                        'tour-ninh-binh-trang-an-2n1d',
                        jsonb_build_object(
                            'id', 'tour-ninh-binh-trang-an-2n1d',
                            'name', 'Tour Ninh Bình - Tràng An 2 ngày 1 đêm',
                            'destination', 'Ninh Bình',
                            'description', 'Tràng An, Bái Đính, Hang Múa, Tam Cốc và khung cảnh non nước hữu tình.',
                            'price', 2190000,
                            'duration', '2 ngày 1 đêm',
                            'start_date', to_char(current_date + interval '40 days', 'YYYY-MM-DD'),
                            'end_date', to_char(current_date + interval '41 days', 'YYYY-MM-DD'),
                            'slots', 28,
                            'sold', 0,
                            'status', 'Đang bán',
                            'image', '/main_site_image/back3.png',
                            'created_at', now(),
                            'updated_at', now()
                        ),
                        now(),
                        now()
                    ),
                    (
                        'tours',
                        'tour-ca-mau-dat-mui-3n2d',
                        jsonb_build_object(
                            'id', 'tour-ca-mau-dat-mui-3n2d',
                            'name', 'Tour Cà Mau - Đất Mũi 3 ngày 2 đêm',
                            'destination', 'Cà Mau',
                            'description', 'Chinh phục cực Nam Tổ quốc, rừng ngập mặn, mốc tọa độ và ẩm thực miền Tây.',
                            'price', 3090000,
                            'duration', '3 ngày 2 đêm',
                            'start_date', to_char(current_date + interval '42 days', 'YYYY-MM-DD'),
                            'end_date', to_char(current_date + interval '44 days', 'YYYY-MM-DD'),
                            'slots', 24,
                            'sold', 0,
                            'status', 'Đang bán',
                            'image', '/main_site_image/back4.png',
                            'created_at', now(),
                            'updated_at', now()
                        ),
                        now(),
                        now()
                    ),
                    (
                        'tours',
                        'tour-hue-da-nang-ba-na-4n3d',
                        jsonb_build_object(
                            'id', 'tour-hue-da-nang-ba-na-4n3d',
                            'name', 'Tour Huế - Đà Nẵng - Bà Nà 4 ngày 3 đêm',
                            'destination', 'Huế, Đà Nẵng',
                            'description', 'Kết hợp di sản cố đô, biển Mỹ Khê, Bà Nà Hills và trải nghiệm ẩm thực miền Trung.',
                            'price', 4290000,
                            'duration', '4 ngày 3 đêm',
                            'start_date', to_char(current_date + interval '44 days', 'YYYY-MM-DD'),
                            'end_date', to_char(current_date + interval '47 days', 'YYYY-MM-DD'),
                            'slots', 24,
                            'sold', 0,
                            'status', 'Đang bán',
                            'image', '/main_site_image/back5.png',
                            'created_at', now(),
                            'updated_at', now()
                        ),
                        now(),
                        now()
                    )
            on conflict (collection, id) do nothing;

            update app_documents
            set data = data || jsonb_build_object(
                    'start_date', coalesce(nullif(data->>'start_date', ''), to_char(current_date + interval '7 days', 'YYYY-MM-DD')),
                    'end_date', coalesce(nullif(data->>'end_date', ''), to_char(current_date + interval '9 days', 'YYYY-MM-DD'))
                ),
                updated_at = now()
            where collection = 'tours'
              and (coalesce(data->>'start_date', '') = '' or coalesce(data->>'end_date', '') = '');
            """;
        await cmd.ExecuteNonQueryAsync();
    }
}
