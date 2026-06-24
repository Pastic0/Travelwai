Thư mục dự phòng cho ảnh/tệp upload của TravelwAI.

Nếu đã cấu hình Supabase Storage trên Render, ảnh upload mới sẽ được lưu lâu dài trên Supabase Storage thay vì phụ thuộc vào ổ đĩa Render.
Nếu chưa có Supabase__StorageApiKey hoặc Supabase Storage lỗi, hệ thống sẽ tự lưu dự phòng vào wwwroot/uploads để web vẫn dùng được.

Các thư mục cũ:
- memories: ảnh kỷ niệm trên bản đồ Việt Nam
- tours: ảnh tour
- profiles: ảnh đại diện
- posts: ảnh bài viết
- chat: tệp đính kèm tin nhắn
