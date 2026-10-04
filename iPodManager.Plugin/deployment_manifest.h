#pragma once
#include <algorithm>
#include <array>
#include <cstdint>
#include <cstring>
#include <limits>
#include <string>
#include <type_traits>
#include <unordered_set>
#include <utility>
#include <vector>

namespace ipod::deployment
{
constexpr std::array<std::uint8_t, 8> kMagic{{'M', 'G', 'S', '4', 'I', 'P', 'D', 0}};
constexpr std::uint16_t kHeaderSize = 40;
constexpr std::uint16_t kStockCount = 73;
enum class Classification : std::uint8_t
{
    Music = 0,
    Podcast = 1
};
class Reader
{
  public:
    Reader(const std::uint8_t *data, std::size_t size) : data_(data), size_(size)
    {
    }
    bool Take(void *out, std::size_t count)
    {
        if (count > size_ - offset_)
            return false;
        std::memcpy(out, data_ + offset_, count);
        offset_ += count;
        return true;
    }
    template <class T> bool Le(T &value)
    {
        static_assert(std::is_integral_v<T>);
        std::uint8_t bytes[sizeof(T)]{};
        if (!Take(bytes, sizeof(bytes)))
            return false;
        using U = std::make_unsigned_t<T>;
        U result = 0;
        for (std::size_t i = 0; i < sizeof(T); ++i)
            result |= U(bytes[i]) << (i * 8);
        value = static_cast<T>(result);
        return true;
    }
    bool String(std::string &value, std::uint16_t length)
    {
        if (length > size_ - offset_)
            return false;
        value.assign(reinterpret_cast<const char *>(data_ + offset_), length);
        offset_ += length;
        return true;
    }
    std::size_t offset() const
    {
        return offset_;
    }

  private:
    const std::uint8_t *data_{};
    std::size_t size_{};
    std::size_t offset_{};
};

inline bool AllZero(const std::uint8_t *p, std::size_t n)
{
    for (std::size_t i = 0; i < n; ++i)
        if (p[i])
            return false;
    return true;
}

inline bool StrictUtf8(const std::string &s)
{
    for (std::size_t i = 0; i < s.size();)
    {
        const auto c = static_cast<std::uint8_t>(s[i]);
        if (!c)
            return false;
        std::uint32_t cp = 0;
        std::size_t n = 0;
        if (c <= 0x7f)
        {
            cp = c;
            n = 1;
        }
        else if (c >= 0xc2 && c <= 0xdf)
        {
            cp = c & 0x1f;
            n = 2;
        }
        else if (c >= 0xe0 && c <= 0xef)
        {
            cp = c & 0x0f;
            n = 3;
        }
        else if (c >= 0xf0 && c <= 0xf4)
        {
            cp = c & 0x07;
            n = 4;
        }
        else
            return false;
        if (n > s.size() - i)
            return false;
        for (std::size_t j = 1; j < n; ++j)
        {
            const auto d = static_cast<std::uint8_t>(s[i + j]);
            if ((d & 0xc0) != 0x80)
                return false;
            cp = (cp << 6) | (d & 0x3f);
        }
        if ((n == 3 && cp < 0x800) || (n == 4 && cp < 0x10000) || cp > 0x10ffff || (cp >= 0xd800 && cp <= 0xdfff))
            return false;
        i += n;
    }
    return true;
}

inline bool SafeRuntimeId(const std::string &s)
{
    if (s.empty() || s.size() > 31)
        return false;
    for (unsigned char c : s)
        if (!((c >= 'A' && c <= 'Z') || (c >= 'a' && c <= 'z') || (c >= '0' && c <= '9') || c == '_' || c == '-'))
            return false;
    return true;
}

inline bool SafeSourcePath(const std::string &s)
{
    if (s.empty() || s.size() > 1024 || !StrictUtf8(s) || s[0] == '/' || s[0] == '\\' ||
        s.find(':') != std::string::npos)
        return false;
    if (s.size() >= 2 && s[1] == ':')
        return false;
    std::size_t start = 0;
    while (start <= s.size())
    {
        const auto end = s.find_first_of("/\\", start);
        const auto length = (end == std::string::npos ? s.size() : end) - start;
        if (!length || (length == 1 && s[start] == '.') || (length == 2 && s[start] == '.' && s[start + 1] == '.'))
            return false;
        if (end == std::string::npos)
            break;
        start = end + 1;
    }
    return true;
}

inline bool SafeBankName(const std::string &s)
{
    if (s.size() < 6 || s.size() > 128 || !StrictUtf8(s) || s.find('/') != std::string::npos ||
        s.find('\\') != std::string::npos || s.find(':') != std::string::npos || s.find("..") != std::string::npos)
        return false;
    return _stricmp(s.c_str() + s.size() - 5, ".bank") == 0;
}

} // namespace ipod::deployment

namespace ipod::deployment::current
{
constexpr std::uint16_t kSchemaVersion = 1, kEntryFixedSize = 196, kNumericCapacity = 951;
struct Entry
{
    Classification classification{};
    std::uint64_t duration_seconds{};
    std::array<std::uint8_t, 16> source_id{};
    std::array<std::uint8_t, 32> source_sha256{};
    std::array<std::uint8_t, 32> bank_sha256{};
    std::uint64_t bank_size{};
    std::uint32_t conversion_profile{};
    std::uint32_t template_profile{};
    std::string runtime_id;
    std::string source_path;
    std::string bank_name;
    std::string title;
    std::string artist;
    std::string album;
    std::uint32_t control_id{};
    std::uint64_t dbm_size{};
    std::array<std::uint8_t, 16> event_guid{};
    std::array<std::uint8_t, 32> dbm_sha256{};
    std::uint32_t builder_version{};
    std::string dbm_path, dbm_request_path, descriptor_event_path, builder_identity;
};
struct Manifest
{
    std::uint64_t generation{};
    std::array<std::uint8_t, kStockCount> stock_enabled{};
    std::vector<Entry> entries;
};
enum class Error
{
    None,
    BaseHeader,
    UnsupportedVersion,
    Count,
    Entry,
    StableIdentity,
    DuplicateStableIdentity,
    ControlBand,
    DuplicateControl,
    DuplicateDescriptor,
    Hash,
    Path,
    DuplicateEventPath,
    Builder,
    TrailingData,
    DbmArtifact,
    BankArtifact,
    BankGraph,
    EventGuid
};
inline bool IsControl(std::uint32_t v)
{
    return (v >= 9000 && v <= 9299) || (v >= 9600 && v <= 9899) || (v >= 10200 && v <= 10499) ||
           (v >= 10800 && v <= 10850);
}
inline std::uint32_t Descriptor(std::uint32_t v)
{
    return v + 300;
}
inline bool SafeEvent(const std::string &s)
{
    return s.size() > 5 && s.size() <= 255 && s.rfind("/BGM/", 0) == 0 && s.find("//") == std::string::npos &&
           StrictUtf8(s);
}
inline bool Parse(const std::uint8_t *data, std::size_t size, Manifest &out, Error &error)
{
    out = {};
    error = Error::None;
    auto fail = [&](Error e) {
        out = {};
        error = e;
        return false;
    };
    if (!data || size < kHeaderSize)
        return fail(Error::BaseHeader);
    Reader r(data, size);
    std::array<std::uint8_t, 8> magic{};
    std::uint16_t schema = 0, header = 0, stock = 0, count = 0, stockbytes = 0, res16 = 0;
    std::uint32_t total = 0, profile = 0, entrybytes = 0;
    if (!r.Take(magic.data(), 8) || !r.Le(schema) || !r.Le(header) || !r.Le(total) || !r.Le(out.generation) ||
        !r.Le(profile) || !r.Le(stock) || !r.Le(count) || !r.Le(stockbytes) || !r.Le(res16) || !r.Le(entrybytes))
        return fail(Error::BaseHeader);
    if (magic != kMagic || schema != 1 || header != 40 || total != size || profile != 1 || stock != 73 ||
        stockbytes != 73 || res16)
        return fail(schema != 1 ? Error::UnsupportedVersion : Error::BaseHeader);
    if (count > 951 || entrybytes != size - 40 - 73)
        return fail(Error::Count);
    if (!r.Take(out.stock_enabled.data(), 73))
        return fail(Error::BaseHeader);
    for (auto x : out.stock_enabled)
        if (x > 1)
            return fail(Error::BaseHeader);
    std::unordered_set<std::string> runtime, events;
    std::unordered_set<std::uint32_t> controls, descriptors;
    std::unordered_set<std::string> stable;
    for (unsigned n = 0; n < count; n++)
    {
        auto start = r.offset();
        Entry e{};
        std::uint32_t len = 0;
        std::uint8_t cls = 0, flags = 0;
        std::uint16_t reserved = 0;
        std::array<std::uint16_t, 10> ls{};
        if (!r.Le(len) || !r.Le(cls) || !r.Le(flags) || !r.Le(reserved) || !r.Le(e.duration_seconds) ||
            !r.Take(e.source_id.data(), 16) || !r.Take(e.source_sha256.data(), 32) ||
            !r.Take(e.bank_sha256.data(), 32) || !r.Le(e.bank_size) || !r.Le(e.conversion_profile) ||
            !r.Le(e.template_profile) || !r.Le(e.control_id) || !r.Le(e.dbm_size) || !r.Take(e.event_guid.data(), 16) ||
            !r.Take(e.dbm_sha256.data(), 32) || !r.Le(e.builder_version))
            return fail(Error::Entry);
        for (auto &x : ls)
            if (!r.Le(x))
                return fail(Error::Entry);
        std::uint64_t variable = 0;
        for (auto x : ls)
            variable += x;
        if (len != 196 + variable || len > size - start || cls > 1 || flags != (cls ? 5 : 1) || reserved)
            return fail(Error::Entry);
        std::string *ss[] = {&e.runtime_id,      &e.source_path,      &e.bank_name,
                             &e.title,           &e.artist,           &e.album,
                             &e.dbm_path,        &e.dbm_request_path, &e.descriptor_event_path,
                             &e.builder_identity};
        for (int i = 0; i < 10; i++)
            if (!r.String(*ss[i], ls[i]) || !StrictUtf8(*ss[i]))
                return fail(Error::Entry);
        std::string stableKey(reinterpret_cast<char *>(e.source_id.data()), 16);
        if (AllZero(e.source_id.data(), 16))
            return fail(Error::StableIdentity);
        if (!stable.insert(stableKey).second)
            return fail(Error::DuplicateStableIdentity);
        if (!IsControl(e.control_id))
            return fail(Error::ControlBand);
        if (!controls.insert(e.control_id).second)
            return fail(Error::DuplicateControl);
        if (!descriptors.insert(Descriptor(e.control_id)).second)
            return fail(Error::DuplicateDescriptor);
        if (!e.duration_seconds || !e.bank_size || !e.dbm_size || !e.conversion_profile || !e.template_profile ||
            AllZero(e.source_sha256.data(), 32) || AllZero(e.bank_sha256.data(), 32) ||
            AllZero(e.dbm_sha256.data(), 32) || AllZero(e.event_guid.data(), 16))
            return fail(Error::Hash);
        if (!SafeRuntimeId(e.runtime_id) || !runtime.insert(e.runtime_id).second || !SafeSourcePath(e.source_path) ||
            !SafeSourcePath(e.dbm_path) || !SafeBankName(e.bank_name) ||
            e.dbm_request_path.rfind("common/dbm/", 0) != 0 || e.dbm_request_path.size() < 5 ||
            e.dbm_request_path.substr(e.dbm_request_path.size() - 4) != ".dbm" || !SafeEvent(e.descriptor_event_path))
            return fail(Error::Path);
        if (!events.insert(e.descriptor_event_path).second)
            return fail(Error::DuplicateEventPath);
        if (e.builder_version != 2 || e.builder_identity.empty() || e.builder_identity.size() > 128)
            return fail(Error::Builder);
        e.classification = static_cast<Classification>(cls);
        out.entries.push_back(std::move(e));
    }
    if (r.offset() != size)
        return fail(Error::TrailingData);
    return true;
}

using Guid = std::array<std::uint8_t, 16>;
struct Role
{
    const std::size_t *offsets;
    std::size_t count;
};
inline std::uint64_t Fnv(const std::string &s)
{
    std::uint64_t h = 14695981039346656037ull;
    for (unsigned char c : s)
    {
        h ^= c;
        h *= 1099511628211ull;
    }
    return h;
}
inline std::uint64_t Mix(std::uint64_t &s)
{
    auto v = (s += 0x9e3779b97f4a7c15ull);
    v = (v ^ (v >> 30)) * 0xbf58476d1ce4e5b9ull;
    v = (v ^ (v >> 27)) * 0x94d049bb133111ebull;
    return v ^ (v >> 31);
}
inline Guid Derive(const std::string &id, const char *role)
{
    auto s = Fnv("MGS4-iPod-BANK-v2:" + id + ":" + role);
    Guid g{};
    auto a = Mix(s), b = Mix(s);
    std::memcpy(g.data(), &a, 8);
    std::memcpy(g.data() + 8, &b, 8);
    g[7] = (g[7] & 15) | 64;
    g[8] = (g[8] & 63) | 128;
    return g;
}
inline bool ValidateDbm(const Entry &e, const std::vector<std::uint8_t> &b)
{
    if (b.size() != e.dbm_size || b.size() <= 0x830 || std::memcmp(b.data(), "DLBM", 4) || b.size() % 0x800)
        return false;
    std::uint32_t id = 0;
    std::memcpy(&id, b.data() + 0x82c, 4);
    return id == e.control_id;
}
inline bool ValidateArtifactHashes(const Entry &e, const std::array<std::uint8_t, 32> &dbm,
                                   const std::array<std::uint8_t, 32> &bank)
{
    return dbm == e.dbm_sha256 && bank == e.bank_sha256;
}
inline Guid DiskToRfc(Guid g)
{
    return Guid{{g[3], g[2], g[1], g[0], g[5], g[4], g[7], g[6], g[8], g[9], g[10], g[11], g[12], g[13], g[14], g[15]}};
}
inline bool ValidateBankGraph(const Entry &e, const std::vector<std::uint8_t> &b)
{
    if (b.size() != e.bank_size || b.size() <= 0x720 || std::memcmp(b.data(), "RIFF", 4) ||
        std::memcmp(b.data() + 8, "FEV ", 4) || std::memcmp(b.data() + 0x720, "FSB5", 4))
        return false;
    static const char *names[] = {"bank",  "input-bus", "global-bus", "master-bus", "bus-effect",
                                  "event", "timeline",  "wave-table", "wave"};
    static const std::size_t offsets[][3] = {{0x30, 0, 0},          {0x7c, 0x3e0, 0},  {0x112, 0x427, 0x5dc},
                                             {0x124, 0x1c0, 0x3f0}, {0x200, 0x26e, 0}, {0x3b0, 0x6ca, 0},
                                             {0x3d0, 0x47c, 0x586}, {0x492, 0x55e, 0}, {0x56e, 0x664, 0x6de}};
    static const int counts[] = {1, 2, 3, 3, 2, 2, 3, 2, 3};
    std::array<Guid, 9> ids{};
    for (int i = 0; i < 9; i++)
    {
        ids[i] = Derive(e.builder_identity, names[i]);
        for (int j = 0; j < counts[i]; j++)
            if (std::memcmp(b.data() + offsets[i][j], ids[i].data(), 16))
                return false;
        for (int k = 0; k < i; k++)
            if (ids[i] == ids[k])
                return false;
    }
    return DiskToRfc(ids[5]) == e.event_guid;
}

} // namespace ipod::deployment::current
