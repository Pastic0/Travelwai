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

            create table if not exists travel_guide_sources (
                id text primary key,
                parent_source_id text,
                name text not null,
                source_type text not null default 'custom',
                url text,
                publisher text,
                reliability integer not null default 60,
                access_level text not null default 'public',
                license text,
                status text not null default 'active',
                created_by text,
                created_at timestamptz not null default now(),
                updated_at timestamptz not null default now()
            );

            create index if not exists ix_travel_guide_sources_status
                on travel_guide_sources(status);

            alter table travel_guide_sources
                add column if not exists parent_source_id text;

            create index if not exists ix_travel_guide_sources_type
                on travel_guide_sources(source_type);

            create index if not exists ix_travel_guide_sources_parent
                on travel_guide_sources(parent_source_id);

            create table if not exists travel_guide_documents (
                id text primary key,
                source_id text not null references travel_guide_sources(id) on delete cascade,
                title text not null,
                url text,
                document_type text not null default 'text',
                content_hash text not null,
                raw_text text not null,
                metadata jsonb not null default '{}'::jsonb,
                status text not null default 'active',
                created_by text,
                created_at timestamptz not null default now(),
                updated_at timestamptz not null default now()
            );

            create index if not exists ix_travel_guide_documents_source
                on travel_guide_documents(source_id);

            create index if not exists ix_travel_guide_documents_status
                on travel_guide_documents(status);

            create index if not exists ix_travel_guide_documents_updated
                on travel_guide_documents(updated_at desc);

            create table if not exists travel_guide_chunks (
                id text primary key,
                document_id text references travel_guide_documents(id) on delete cascade,
                source_id text not null references travel_guide_sources(id) on delete cascade,
                title text not null,
                content text not null,
                keywords text,
                search_text text not null,
                url text,
                chunk_index integer not null default 0,
                embedding jsonb,
                embedding_model text,
                embedding_updated_at timestamptz,
                status text not null default 'active',
                created_at timestamptz not null default now(),
                updated_at timestamptz not null default now()
            );

            create index if not exists ix_travel_guide_chunks_source
                on travel_guide_chunks(source_id);

            create index if not exists ix_travel_guide_chunks_document
                on travel_guide_chunks(document_id);

            create index if not exists ix_travel_guide_chunks_status
                on travel_guide_chunks(status);

            alter table travel_guide_chunks
                add column if not exists embedding jsonb;

            alter table travel_guide_chunks
                add column if not exists embedding_model text;

            alter table travel_guide_chunks
                add column if not exists embedding_updated_at timestamptz;

            create index if not exists ix_travel_guide_chunks_embedding_model
                on travel_guide_chunks(embedding_model);

            create index if not exists ix_travel_guide_chunks_updated
                on travel_guide_chunks(updated_at desc);

            create index if not exists ix_travel_guide_chunks_status_reliability
                on travel_guide_chunks(status, updated_at desc);

            create index if not exists ix_travel_guide_chunks_search_vector
                on travel_guide_chunks using gin(to_tsvector('simple', search_text));

            create index if not exists ix_travel_guide_documents_status_updated
                on travel_guide_documents(status, updated_at desc);


            insert into travel_guide_sources(id, parent_source_id, name, source_type, url, publisher, reliability, access_level, license, status)
            values
                ('cuc-di-san-van-hoa', null, 'Cục Di sản Văn hoá', 'official_heritage', 'https://dsvh.gov.vn', 'Bộ Văn hoá, Thể thao và Du lịch', 98, 'public', 'public_reference', 'active'),
                ('bo-vhttdl', null, 'Bộ Văn hoá, Thể thao và Du lịch', 'official_ministry', 'https://bvhttdl.gov.vn', 'Bộ Văn hoá, Thể thao và Du lịch', 96, 'public', 'public_reference', 'active'),
                ('unesco-world-heritage', null, 'UNESCO World Heritage', 'unesco', 'https://whc.unesco.org', 'UNESCO', 98, 'public', 'public_reference', 'active'),
                ('unesco-ich', null, 'UNESCO Intangible Cultural Heritage', 'unesco', 'https://ich.unesco.org', 'UNESCO', 98, 'public', 'public_reference', 'active'),
                ('van-ban-chinh-phu', null, 'Văn bản Chính phủ', 'legal', 'https://vanban.chinhphu.vn', 'Chính phủ Việt Nam', 96, 'public', 'public_reference', 'active'),
                ('co-so-du-lieu-phap-luat', null, 'Cơ sở dữ liệu quốc gia về pháp luật', 'legal', 'https://vbpl.vn', 'Bộ Tư pháp', 96, 'public', 'public_reference', 'active'),
                ('cuc-du-lich-quoc-gia', null, 'Cục Du lịch Quốc gia Việt Nam', 'official_tourism', 'https://vietnamtourism.gov.vn', 'Cục Du lịch Quốc gia Việt Nam', 88, 'public', 'public_reference', 'active'),
                ('ubnd-so-dia-phuong', null, 'UBND và Sở địa phương', 'local_government', null, 'UBND/Sở địa phương', 90, 'public', 'public_reference', 'active'),
                ('bao-tang-ban-quan-ly', null, 'Bảo tàng và ban quản lý di tích', 'museum_management', null, 'Bảo tàng/Ban quản lý di tích', 85, 'public_or_internal', 'mixed', 'active'),
                ('dia-chi-nghien-cuu', null, 'Địa chí, sách và nghiên cứu', 'research', null, 'Thư viện/Viện nghiên cứu/Trường đại học', 80, 'restricted', 'check_license', 'active'),
                ('tu-lieu-thuc-dia', null, 'Tư liệu thực địa và phỏng vấn', 'fieldwork', null, 'TravelwAI/Nghệ nhân/Cộng đồng địa phương', 76, 'internal', 'permission_required', 'active'),
                ('bao-chi-chinh-thong', null, 'Báo chí chính thống', 'press', null, 'TTXVN/Nhân Dân/VOV/VTV/Báo địa phương', 70, 'public', 'public_reference', 'active'),
                ('wikipedia', null, 'Wikipedia', 'wikipedia', 'https://vi.wikipedia.org', 'Wikimedia Foundation', 42, 'public', 'CC BY-SA / reference_only', 'active'),
                ('nguon-phu-tham-khao', null, 'Nguồn phụ tham khảo', 'secondary_reference', null, 'Blog/Mạng xã hội/Đánh giá người dùng', 38, 'public', 'reference_only', 'active'),
                ('danh-muc-di-tich-quoc-gia-dac-biet', 'cuc-di-san-van-hoa', 'Danh mục di tích quốc gia đặc biệt', 'official_heritage_catalog', 'https://dsvh.gov.vn/danh-muc-di-tich-quoc-gia-dac-biet-1752', 'Cục Di sản Văn hoá', 99, 'public', 'public_reference', 'active'),
                ('danh-muc-di-san-van-hoa-phi-vat-the', 'cuc-di-san-van-hoa', 'Danh mục di sản văn hoá phi vật thể', 'official_heritage_catalog', 'https://dsvh.gov.vn/di-san-van-hoa-phi-vat-the-1563', 'Cục Di sản Văn hoá', 98, 'public', 'public_reference', 'active'),
                ('danh-muc-di-san-the-gioi-viet-nam', 'unesco-world-heritage', 'Danh mục di sản thế giới Việt Nam', 'unesco_catalog', 'https://whc.unesco.org/en/statesparties/vn', 'UNESCO', 98, 'public', 'public_reference', 'active'),
                ('danh-muc-ich-viet-nam', 'unesco-ich', 'Danh mục di sản văn hoá phi vật thể Việt Nam trên UNESCO ICH', 'unesco_catalog', 'https://ich.unesco.org/en/state/viet-nam-VN', 'UNESCO', 98, 'public', 'public_reference', 'active'),
                ('cong-bao-chinh-phu', 'van-ban-chinh-phu', 'Công báo điện tử Chính phủ', 'legal', 'https://congbao.chinhphu.vn', 'Chính phủ Việt Nam', 95, 'public', 'public_reference', 'active'),
                ('cong-thong-tin-quoc-hoi', 'co-so-du-lieu-phap-luat', 'Cổng thông tin điện tử Quốc hội', 'legal', 'https://quochoi.vn', 'Quốc hội Việt Nam', 94, 'public', 'public_reference', 'active'),
                ('bo-tu-phap', 'co-so-du-lieu-phap-luat', 'Bộ Tư pháp', 'legal', 'https://moj.gov.vn', 'Bộ Tư pháp', 94, 'public', 'public_reference', 'active'),
                ('vietnam-travel', 'cuc-du-lich-quoc-gia', 'Vietnam Travel', 'official_tourism', 'https://vietnam.travel', 'Cục Du lịch Quốc gia Việt Nam', 86, 'public', 'public_reference', 'active'),
                ('trung-tam-thong-tin-du-lich', 'cuc-du-lich-quoc-gia', 'Trung tâm Thông tin du lịch', 'official_tourism', 'https://titc.vn', 'Cục Du lịch Quốc gia Việt Nam', 86, 'public', 'public_reference', 'active'),
                ('chinhphu-thong-tin-tinh-thanh', 'ubnd-so-dia-phuong', 'Thông tin tỉnh thành - Cổng TTĐT Chính phủ', 'local_government_index', 'https://chinhphu.vn/thong-tin-tinh-thanh', 'Cổng Thông tin điện tử Chính phủ', 93, 'public', 'public_reference', 'active'),
                ('ubnd-ha-noi', 'ubnd-so-dia-phuong', 'Cổng TTĐT Thành phố Hà Nội', 'local_government', 'https://hanoi.gov.vn', 'UBND Thành phố Hà Nội', 90, 'public', 'public_reference', 'active'),
                ('ubnd-ho-chi-minh', 'ubnd-so-dia-phuong', 'Cổng TTĐT Thành phố Hồ Chí Minh', 'local_government', 'https://tphcm.gov.vn', 'UBND Thành phố Hồ Chí Minh', 90, 'public', 'public_reference', 'active'),
                ('ubnd-hai-phong', 'ubnd-so-dia-phuong', 'Cổng TTĐT Thành phố Hải Phòng', 'local_government', 'https://haiphong.gov.vn', 'UBND Thành phố Hải Phòng', 90, 'public', 'public_reference', 'active'),
                ('ubnd-da-nang', 'ubnd-so-dia-phuong', 'Cổng TTĐT Thành phố Đà Nẵng', 'local_government', 'https://danang.gov.vn', 'UBND Thành phố Đà Nẵng', 90, 'public', 'public_reference', 'active'),
                ('ubnd-can-tho', 'ubnd-so-dia-phuong', 'Cổng TTĐT Thành phố Cần Thơ', 'local_government', 'https://cantho.gov.vn', 'UBND Thành phố Cần Thơ', 90, 'public', 'public_reference', 'active'),
                ('ubnd-hue', 'ubnd-so-dia-phuong', 'Cổng TTĐT Thành phố Huế', 'local_government', 'https://hue.gov.vn', 'UBND Thành phố Huế', 90, 'public', 'public_reference', 'active'),
                ('ubnd-dong-nai', 'ubnd-so-dia-phuong', 'Cổng TTĐT Đồng Nai', 'local_government', 'https://dongnai.gov.vn', 'UBND Đồng Nai', 90, 'public', 'public_reference', 'active'),
                ('ubnd-an-giang', 'ubnd-so-dia-phuong', 'Cổng TTĐT An Giang', 'local_government', 'https://angiang.gov.vn', 'UBND An Giang', 90, 'public', 'public_reference', 'active'),
                ('ubnd-bac-ninh', 'ubnd-so-dia-phuong', 'Cổng TTĐT Bắc Ninh', 'local_government', 'https://bacninh.gov.vn', 'UBND Bắc Ninh', 90, 'public', 'public_reference', 'active'),
                ('ubnd-cao-bang', 'ubnd-so-dia-phuong', 'Cổng TTĐT Cao Bằng', 'local_government', 'https://caobang.gov.vn', 'UBND Cao Bằng', 90, 'public', 'public_reference', 'active'),
                ('ubnd-ca-mau', 'ubnd-so-dia-phuong', 'Cổng TTĐT Cà Mau', 'local_government', 'https://camau.gov.vn', 'UBND Cà Mau', 90, 'public', 'public_reference', 'active'),
                ('ubnd-dien-bien', 'ubnd-so-dia-phuong', 'Cổng TTĐT Điện Biên', 'local_government', 'https://dienbien.gov.vn', 'UBND Điện Biên', 90, 'public', 'public_reference', 'active'),
                ('ubnd-dak-lak', 'ubnd-so-dia-phuong', 'Cổng TTĐT Đắk Lắk', 'local_government', 'https://daklak.gov.vn', 'UBND Đắk Lắk', 90, 'public', 'public_reference', 'active'),
                ('ubnd-dong-thap', 'ubnd-so-dia-phuong', 'Cổng TTĐT Đồng Tháp', 'local_government', 'https://dongthap.gov.vn', 'UBND Đồng Tháp', 90, 'public', 'public_reference', 'active'),
                ('ubnd-gia-lai', 'ubnd-so-dia-phuong', 'Cổng TTĐT Gia Lai', 'local_government', 'https://gialai.gov.vn', 'UBND Gia Lai', 90, 'public', 'public_reference', 'active'),
                ('ubnd-ha-tinh', 'ubnd-so-dia-phuong', 'Cổng TTĐT Hà Tĩnh', 'local_government', 'https://hatinh.gov.vn', 'UBND Hà Tĩnh', 90, 'public', 'public_reference', 'active'),
                ('ubnd-hung-yen', 'ubnd-so-dia-phuong', 'Cổng TTĐT Hưng Yên', 'local_government', 'https://hungyen.gov.vn', 'UBND Hưng Yên', 90, 'public', 'public_reference', 'active'),
                ('ubnd-khanh-hoa', 'ubnd-so-dia-phuong', 'Cổng TTĐT Khánh Hòa', 'local_government', 'https://khanhhoa.gov.vn', 'UBND Khánh Hòa', 90, 'public', 'public_reference', 'active'),
                ('ubnd-lai-chau', 'ubnd-so-dia-phuong', 'Cổng TTĐT Lai Châu', 'local_government', 'https://laichau.gov.vn', 'UBND Lai Châu', 90, 'public', 'public_reference', 'active'),
                ('ubnd-lao-cai', 'ubnd-so-dia-phuong', 'Cổng TTĐT Lào Cai', 'local_government', 'https://laocai.gov.vn', 'UBND Lào Cai', 90, 'public', 'public_reference', 'active'),
                ('ubnd-lam-dong', 'ubnd-so-dia-phuong', 'Cổng TTĐT Lâm Đồng', 'local_government', 'https://lamdong.gov.vn', 'UBND Lâm Đồng', 90, 'public', 'public_reference', 'active'),
                ('ubnd-lang-son', 'ubnd-so-dia-phuong', 'Cổng TTĐT Lạng Sơn', 'local_government', 'https://langson.gov.vn', 'UBND Lạng Sơn', 90, 'public', 'public_reference', 'active'),
                ('ubnd-nghe-an', 'ubnd-so-dia-phuong', 'Cổng TTĐT Nghệ An', 'local_government', 'https://nghean.gov.vn', 'UBND Nghệ An', 90, 'public', 'public_reference', 'active'),
                ('ubnd-ninh-binh', 'ubnd-so-dia-phuong', 'Cổng TTĐT Ninh Bình', 'local_government', 'https://ninhbinh.gov.vn', 'UBND Ninh Bình', 90, 'public', 'public_reference', 'active'),
                ('ubnd-phu-tho', 'ubnd-so-dia-phuong', 'Cổng TTĐT Phú Thọ', 'local_government', 'https://phutho.gov.vn', 'UBND Phú Thọ', 90, 'public', 'public_reference', 'active'),
                ('ubnd-quang-ngai', 'ubnd-so-dia-phuong', 'Cổng TTĐT Quảng Ngãi', 'local_government', 'https://quangngai.gov.vn', 'UBND Quảng Ngãi', 90, 'public', 'public_reference', 'active'),
                ('ubnd-quang-ninh', 'ubnd-so-dia-phuong', 'Cổng TTĐT Quảng Ninh', 'local_government', 'https://quangninh.gov.vn', 'UBND Quảng Ninh', 90, 'public', 'public_reference', 'active'),
                ('ubnd-quang-tri', 'ubnd-so-dia-phuong', 'Cổng TTĐT Quảng Trị', 'local_government', 'https://quangtri.gov.vn', 'UBND Quảng Trị', 90, 'public', 'public_reference', 'active'),
                ('ubnd-son-la', 'ubnd-so-dia-phuong', 'Cổng TTĐT Sơn La', 'local_government', 'https://sonla.gov.vn', 'UBND Sơn La', 90, 'public', 'public_reference', 'active'),
                ('ubnd-thanh-hoa', 'ubnd-so-dia-phuong', 'Cổng TTĐT Thanh Hóa', 'local_government', 'https://thanhhoa.gov.vn', 'UBND Thanh Hóa', 90, 'public', 'public_reference', 'active'),
                ('ubnd-thai-nguyen', 'ubnd-so-dia-phuong', 'Cổng TTĐT Thái Nguyên', 'local_government', 'https://thainguyen.gov.vn', 'UBND Thái Nguyên', 90, 'public', 'public_reference', 'active'),
                ('ubnd-tuyen-quang', 'ubnd-so-dia-phuong', 'Cổng TTĐT Tuyên Quang', 'local_government', 'https://tuyenquang.gov.vn', 'UBND Tuyên Quang', 90, 'public', 'public_reference', 'active'),
                ('ubnd-tay-ninh', 'ubnd-so-dia-phuong', 'Cổng TTĐT Tây Ninh', 'local_government', 'https://tayninh.gov.vn', 'UBND Tây Ninh', 90, 'public', 'public_reference', 'active'),
                ('ubnd-vinh-long', 'ubnd-so-dia-phuong', 'Cổng TTĐT Vĩnh Long', 'local_government', 'https://vinhlong.gov.vn', 'UBND Vĩnh Long', 90, 'public', 'public_reference', 'active'),
                ('du-lich-ha-noi', 'cuc-du-lich-quoc-gia', 'Du lịch Hà Nội', 'local_tourism', 'https://sodulich.hanoi.gov.vn', 'Sở Du lịch Hà Nội', 82, 'public', 'public_reference', 'active'),
                ('du-lich-tphcm', 'cuc-du-lich-quoc-gia', 'Sở Du lịch Thành phố Hồ Chí Minh', 'local_tourism', 'https://sodulich.hochiminhcity.gov.vn', 'Sở Du lịch Thành phố Hồ Chí Minh', 82, 'public', 'public_reference', 'active'),
                ('du-lich-da-nang', 'cuc-du-lich-quoc-gia', 'Du lịch Đà Nẵng', 'local_tourism', 'https://danangfantasticity.com', 'Sở Du lịch Thành phố Đà Nẵng', 82, 'public', 'public_reference', 'active'),
                ('du-lich-hue', 'cuc-du-lich-quoc-gia', 'Khám phá Huế', 'local_tourism', 'https://khamphahue.com.vn', 'Thành phố Huế', 82, 'public', 'public_reference', 'active'),
                ('du-lich-quang-ninh', 'cuc-du-lich-quoc-gia', 'Du lịch Quảng Ninh', 'local_tourism', 'https://halongtourism.com.vn', 'Sở Du lịch Quảng Ninh', 82, 'public', 'public_reference', 'active'),
                ('du-lich-ninh-binh', 'cuc-du-lich-quoc-gia', 'Du lịch Ninh Bình', 'local_tourism', 'https://dulichninhbinh.com.vn', 'Sở Du lịch Ninh Bình', 82, 'public', 'public_reference', 'active'),
                ('du-lich-quang-nam', 'cuc-du-lich-quoc-gia', 'Du lịch Quảng Nam', 'local_tourism', 'https://quangnamtourism.com.vn', 'Sở VHTTDL Quảng Nam', 82, 'public', 'public_reference', 'active'),
                ('du-lich-khanh-hoa', 'cuc-du-lich-quoc-gia', 'Du lịch Khánh Hòa', 'local_tourism', 'https://nhatrang-travel.com', 'Sở Du lịch Khánh Hòa', 82, 'public', 'public_reference', 'active'),
                ('du-lich-lam-dong', 'cuc-du-lich-quoc-gia', 'Du lịch Lâm Đồng', 'local_tourism', 'https://dalat-info.gov.vn', 'Sở VHTTDL Lâm Đồng', 82, 'public', 'public_reference', 'active'),
                ('du-lich-can-tho', 'cuc-du-lich-quoc-gia', 'Du lịch Cần Thơ', 'local_tourism', 'https://canthotourism.vn', 'Sở VHTTDL Cần Thơ', 82, 'public', 'public_reference', 'active'),
                ('bao-tang-lich-su-quoc-gia', 'bao-tang-ban-quan-ly', 'Bảo tàng Lịch sử Quốc gia', 'museum_management', 'https://baotanglichsu.vn', 'Bảo tàng Lịch sử Quốc gia', 88, 'public_or_internal', 'mixed', 'active'),
                ('bao-tang-my-thuat-viet-nam', 'bao-tang-ban-quan-ly', 'Bảo tàng Mỹ thuật Việt Nam', 'museum_management', 'https://vnfam.vn', 'Bảo tàng Mỹ thuật Việt Nam', 84, 'public_or_internal', 'mixed', 'active'),
                ('bao-tang-dan-toc-hoc-viet-nam', 'bao-tang-ban-quan-ly', 'Bảo tàng Dân tộc học Việt Nam', 'museum_management', 'https://vme.org.vn', 'Bảo tàng Dân tộc học Việt Nam', 84, 'public_or_internal', 'mixed', 'active'),
                ('bao-tang-ho-chi-minh', 'bao-tang-ban-quan-ly', 'Bảo tàng Hồ Chí Minh', 'museum_management', 'https://baotanghochiminh.vn', 'Bảo tàng Hồ Chí Minh', 84, 'public_or_internal', 'mixed', 'active'),
                ('bao-tang-phu-nu-viet-nam', 'bao-tang-ban-quan-ly', 'Bảo tàng Phụ nữ Việt Nam', 'museum_management', 'https://baotangphunu.org.vn', 'Bảo tàng Phụ nữ Việt Nam', 82, 'public_or_internal', 'mixed', 'active'),
                ('bao-tang-ha-noi', 'bao-tang-ban-quan-ly', 'Bảo tàng Hà Nội', 'museum_management', 'https://baotanghanoi.com.vn', 'Bảo tàng Hà Nội', 82, 'public_or_internal', 'mixed', 'active'),
                ('bao-tang-da-nang', 'bao-tang-ban-quan-ly', 'Bảo tàng Đà Nẵng', 'museum_management', 'https://baotangdanang.vn', 'Bảo tàng Đà Nẵng', 82, 'public_or_internal', 'mixed', 'active'),
                ('hoang-thanh-thang-long', 'bao-tang-ban-quan-ly', 'Trung tâm Bảo tồn Di sản Thăng Long - Hà Nội', 'museum_management', 'https://hoangthanhthanglong.vn', 'Trung tâm Bảo tồn Di sản Thăng Long - Hà Nội', 88, 'public_or_internal', 'mixed', 'active'),
                ('van-mieu-quoc-tu-giam', 'bao-tang-ban-quan-ly', 'Trung tâm Hoạt động Văn hóa Khoa học Văn Miếu - Quốc Tử Giám', 'museum_management', 'https://vanmieu.gov.vn', 'Trung tâm Hoạt động VHKH Văn Miếu - Quốc Tử Giám', 86, 'public_or_internal', 'mixed', 'active'),
                ('di-tich-co-do-hue', 'bao-tang-ban-quan-ly', 'Trung tâm Bảo tồn Di tích Cố đô Huế', 'museum_management', 'https://hueworldheritage.org.vn', 'Trung tâm Bảo tồn Di tích Cố đô Huế', 90, 'public_or_internal', 'mixed', 'active'),
                ('vinh-ha-long', 'bao-tang-ban-quan-ly', 'Ban Quản lý Vịnh Hạ Long', 'museum_management', 'https://halongbay.com.vn', 'Ban Quản lý Vịnh Hạ Long', 88, 'public_or_internal', 'mixed', 'active'),
                ('my-son-sanctuary', 'bao-tang-ban-quan-ly', 'Ban Quản lý Di sản Văn hoá Mỹ Sơn', 'museum_management', 'https://mysonsanctuary.com.vn', 'Ban Quản lý Di sản Văn hoá Mỹ Sơn', 86, 'public_or_internal', 'mixed', 'active'),
                ('thanh-nha-ho', 'bao-tang-ban-quan-ly', 'Trung tâm Bảo tồn Di sản Thành Nhà Hồ', 'museum_management', 'https://thanhnhaho.vn', 'Trung tâm Bảo tồn Di sản Thành Nhà Hồ', 86, 'public_or_internal', 'mixed', 'active'),
                ('hoi-an-heritage', 'bao-tang-ban-quan-ly', 'Trung tâm Quản lý Bảo tồn Di sản Văn hóa Hội An', 'museum_management', 'https://hoianheritage.net', 'Trung tâm Quản lý Bảo tồn Di sản Văn hóa Hội An', 86, 'public_or_internal', 'mixed', 'active'),
                ('thu-vien-quoc-gia-viet-nam', 'dia-chi-nghien-cuu', 'Thư viện Quốc gia Việt Nam', 'research', 'https://nlv.gov.vn', 'Thư viện Quốc gia Việt Nam', 84, 'restricted', 'check_license', 'active'),
                ('vien-van-hoa-nghe-thuat-quoc-gia', 'dia-chi-nghien-cuu', 'Viện Văn hoá Nghệ thuật quốc gia Việt Nam', 'research', 'https://vicas.org.vn', 'Viện Văn hoá Nghệ thuật quốc gia Việt Nam', 84, 'public_or_restricted', 'check_license', 'active'),
                ('vien-han-lam-khxh-viet-nam', 'dia-chi-nghien-cuu', 'Viện Hàn lâm Khoa học xã hội Việt Nam', 'research', 'https://vass.gov.vn', 'Viện Hàn lâm Khoa học xã hội Việt Nam', 83, 'public_or_restricted', 'check_license', 'active'),
                ('tap-chi-van-hoa-nghe-thuat', 'dia-chi-nghien-cuu', 'Tạp chí Văn hóa Nghệ thuật', 'research', 'https://vhnt.org.vn', 'Bộ Văn hoá, Thể thao và Du lịch', 76, 'public_or_restricted', 'check_license', 'active'),
                ('vjol', 'dia-chi-nghien-cuu', 'Vietnam Journals Online', 'research', 'https://vjol.info.vn', 'Vietnam Journals Online', 74, 'public_or_restricted', 'check_license', 'active'),
                ('thu-vien-so-han-nom', 'dia-chi-nghien-cuu', 'Thư viện số Hán Nôm', 'research', 'https://hannom.nlv.gov.vn', 'Thư viện Quốc gia Việt Nam', 78, 'restricted', 'check_license', 'active'),
                ('travelwai-phong-van-nghe-nhan', 'tu-lieu-thuc-dia', 'TravelwAI - Phỏng vấn nghệ nhân', 'fieldwork', null, 'TravelwAI/Nghệ nhân', 78, 'internal', 'permission_required', 'active'),
                ('travelwai-anh-thuc-dia', 'tu-lieu-thuc-dia', 'TravelwAI - Ảnh thực địa', 'fieldwork', null, 'TravelwAI', 74, 'internal', 'permission_required', 'active'),
                ('travelwai-audio-video-thuc-dia', 'tu-lieu-thuc-dia', 'TravelwAI - Audio/Video thực địa', 'fieldwork', null, 'TravelwAI', 74, 'internal', 'permission_required', 'active'),
                ('travelwai-bang-thuyet-minh-brochure', 'tu-lieu-thuc-dia', 'TravelwAI - Bảng thuyết minh/Brochure đã xin phép', 'fieldwork', null, 'TravelwAI/Ban quản lý điểm đến', 76, 'internal', 'permission_required', 'active'),
                ('bao-chinh-phu', 'bao-chi-chinh-thong', 'Báo điện tử Chính phủ', 'press', 'https://baochinhphu.vn', 'Văn phòng Chính phủ', 84, 'public', 'public_reference', 'active'),
                ('thong-tan-xa-viet-nam', 'bao-chi-chinh-thong', 'Thông tấn xã Việt Nam', 'press', 'https://vnanet.vn', 'Thông tấn xã Việt Nam', 82, 'public', 'public_reference', 'active'),
                ('vietnamplus', 'bao-chi-chinh-thong', 'VietnamPlus', 'press', 'https://vietnamplus.vn', 'Thông tấn xã Việt Nam', 80, 'public', 'public_reference', 'active'),
                ('bao-tin-tuc', 'bao-chi-chinh-thong', 'Báo Tin tức', 'press', 'https://baotintuc.vn', 'Thông tấn xã Việt Nam', 78, 'public', 'public_reference', 'active'),
                ('bao-nhan-dan', 'bao-chi-chinh-thong', 'Báo Nhân Dân', 'press', 'https://nhandan.vn', 'Báo Nhân Dân', 80, 'public', 'public_reference', 'active'),
                ('vov', 'bao-chi-chinh-thong', 'Đài Tiếng nói Việt Nam', 'press', 'https://vov.vn', 'Đài Tiếng nói Việt Nam', 78, 'public', 'public_reference', 'active'),
                ('vtv', 'bao-chi-chinh-thong', 'Đài Truyền hình Việt Nam', 'press', 'https://vtv.vn', 'Đài Truyền hình Việt Nam', 78, 'public', 'public_reference', 'active'),
                ('bao-van-hoa', 'bao-chi-chinh-thong', 'Báo Văn Hoá', 'press', 'https://baovanhoa.vn', 'Bộ Văn hoá, Thể thao và Du lịch', 76, 'public', 'public_reference', 'active'),
                ('bao-to-quoc', 'bao-chi-chinh-thong', 'Báo Tổ Quốc', 'press', 'https://toquoc.vn', 'Bộ Văn hoá, Thể thao và Du lịch', 76, 'public', 'public_reference', 'active'),
                ('bao-quan-doi-nhan-dan', 'bao-chi-chinh-thong', 'Báo Quân đội nhân dân', 'press', 'https://www.qdnd.vn', 'Báo Quân đội nhân dân', 75, 'public', 'public_reference', 'active'),
                ('wikipedia-vi', 'wikipedia', 'Wikipedia tiếng Việt', 'wikipedia', 'https://vi.wikipedia.org', 'Wikimedia Foundation', 42, 'public', 'CC BY-SA / reference_only', 'active'),
                ('wikipedia-en', 'wikipedia', 'Wikipedia tiếng Anh', 'wikipedia', 'https://en.wikipedia.org', 'Wikimedia Foundation', 40, 'public', 'CC BY-SA / reference_only', 'active'),
                ('wikivoyage-vi', 'nguon-phu-tham-khao', 'Wikivoyage tiếng Việt', 'secondary_reference', 'https://vi.wikivoyage.org', 'Wikimedia Foundation', 40, 'public', 'CC BY-SA / reference_only', 'active'),
                ('wikivoyage-en', 'nguon-phu-tham-khao', 'Wikivoyage tiếng Anh', 'secondary_reference', 'https://en.wikivoyage.org', 'Wikimedia Foundation', 38, 'public', 'CC BY-SA / reference_only', 'active'),
                ('thuvienphapluat-tham-khao', 'nguon-phu-tham-khao', 'Thư viện Pháp luật tham khảo', 'secondary_reference', 'https://thuvienphapluat.vn', 'Thư viện Pháp luật', 55, 'public', 'reference_only', 'active')
            on conflict (id) do update
            set parent_source_id = excluded.parent_source_id,
                name = excluded.name,
                source_type = excluded.source_type,
                url = excluded.url,
                publisher = excluded.publisher,
                reliability = excluded.reliability,
                access_level = excluded.access_level,
                license = excluded.license,
                status = excluded.status,
                updated_at = now();


            create table if not exists ai_chat_quota_windows (
                user_id text primary key,
                window_start_utc timestamptz not null,
                count integer not null default 0,
                updated_at timestamptz not null default now()
            );

            create index if not exists ix_ai_chat_quota_windows_updated
                on ai_chat_quota_windows(updated_at desc);

            delete from ai_chat_quota_windows
            where updated_at < now() - interval '1 day';

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
