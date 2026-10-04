#include "../deployment_manifest.h"
#include "../runtime_path.h"
#include <cassert>
#include <cstdint>
#include <cstring>
#include <fstream>
#include <iostream>
#include <iterator>
#include <string>
#include <vector>

using ipod::deployment::current::Manifest;
using ipod::deployment::current::Error;

template <class T> void Put(std::vector<std::uint8_t>& bytes, T value) {
    for (std::size_t i = 0; i < sizeof(T); ++i)
        bytes.push_back(static_cast<std::uint8_t>(value >> (8 * i)));
}

void PutBytes(std::vector<std::uint8_t>& bytes, std::size_t count, std::uint8_t value) {
    bytes.insert(bytes.end(), count, value);
}

std::vector<std::uint8_t> Record(std::uint8_t classification, std::uint8_t flags,
                                 std::uint32_t control, const std::string& identity) {
    const std::string dbm = "common/dbm/" + identity + ".dbm";
    const std::string event = "/BGM/" + identity + "/" + identity;
    const std::string strings[] = {identity, "Custom/test.flac", identity + ".bank", "Title",
                                   "Artist", "Album", dbm, dbm, event, identity};
    std::size_t variable = 0;
    for (const auto& value : strings) variable += value.size();
    std::vector<std::uint8_t> bytes;
    Put<std::uint32_t>(bytes, static_cast<std::uint32_t>(196 + variable));
    Put<std::uint8_t>(bytes, classification);
    Put<std::uint8_t>(bytes, flags);
    Put<std::uint16_t>(bytes, 0);
    Put<std::uint64_t>(bytes, 100);
    PutBytes(bytes, 16, static_cast<std::uint8_t>(control & 0xff));
    PutBytes(bytes, 32, 1);
    PutBytes(bytes, 32, 2);
    Put<std::uint64_t>(bytes, 1000);
    Put<std::uint32_t>(bytes, 2);
    Put<std::uint32_t>(bytes, 1);
    Put<std::uint32_t>(bytes, control);
    Put<std::uint64_t>(bytes, 0x800);
    PutBytes(bytes, 16, static_cast<std::uint8_t>(control & 0xff));
    PutBytes(bytes, 32, 3);
    Put<std::uint32_t>(bytes, 2);
    for (const auto& value : strings)
        Put<std::uint16_t>(bytes, static_cast<std::uint16_t>(value.size()));
    for (const auto& value : strings)
        bytes.insert(bytes.end(), value.begin(), value.end());
    assert(bytes.size() == 196 + variable);
    return bytes;
}

std::vector<std::uint8_t> Deployment(const std::vector<std::vector<std::uint8_t>>& records) {
    std::vector<std::uint8_t> bytes;
    for (auto value : ipod::deployment::kMagic) bytes.push_back(value);
    Put<std::uint16_t>(bytes, 1);
    Put<std::uint16_t>(bytes, 40);
    std::size_t total = 40 + 73;
    for (const auto& record : records) total += record.size();
    Put<std::uint32_t>(bytes, static_cast<std::uint32_t>(total));
    Put<std::uint64_t>(bytes, 1);
    Put<std::uint32_t>(bytes, 1);
    Put<std::uint16_t>(bytes, 73);
    Put<std::uint16_t>(bytes, static_cast<std::uint16_t>(records.size()));
    Put<std::uint16_t>(bytes, 73);
    Put<std::uint16_t>(bytes, 0);
    Put<std::uint32_t>(bytes, static_cast<std::uint32_t>(total - 113));
    PutBytes(bytes, 73, 1);
    for (const auto& record : records)
        bytes.insert(bytes.end(), record.begin(), record.end());
    assert(bytes.size() == total);
    return bytes;
}

int main(int argc, char** argv) {
    {
        const std::string long_relative = "custom/" + std::string(300, 'x') + ".flac";
        std::string playback;
        std::size_t full_length = 0;
        bool extended = false;
        assert(ipod::runtime::BuildPlaybackSourcePath(
            L"C:\\MGS4\\", long_relative, playback, &full_length, &extended));
        assert(full_length > MAX_PATH);
        assert(extended);
        assert(playback.rfind("\\\\?\\C:\\MGS4\\", 0) == 0);
        assert(playback.size() == full_length + 4);
        assert(playback.size() >= long_relative.size());
        assert(playback.find('/') == std::string::npos);
        assert(playback.find("custom\\") != std::string::npos);
    }
    {
        const std::wstring long_game_directory = L"C:\\" + std::wstring(100, L'g') + L"\\";
        const std::string relative = "custom/" + std::string(140, 'x') + ".flac";
        std::string playback;
        std::size_t full_length = 0;
        bool extended = false;
        assert(ipod::runtime::BuildPlaybackSourcePath(long_game_directory.c_str(), relative, playback,
                                                      &full_length, &extended));
        assert(full_length >= MAX_PATH);
        assert(extended);
        assert(playback.rfind("\\\\?\\C:\\", 0) == 0);
        assert(playback.find("iPod\\content\\custom\\") != std::string::npos);
    }

    Manifest manifest;
    Error error{};
    auto empty = Deployment({});
    assert(ipod::deployment::current::Parse(empty.data(), empty.size(), manifest, error));
    assert(manifest.entries.empty());

    auto music = Record(0, 1, 9000, "M4IPOD_A");
    auto podcast = Record(1, 5, 9001, "M4IPOD_B");
    auto mixed = Deployment({music, podcast});
    assert(ipod::deployment::current::Parse(mixed.data(), mixed.size(), manifest, error));
    assert(manifest.entries.size() == 2);
    assert(manifest.entries[0].classification == ipod::deployment::Classification::Music);
    assert(manifest.entries[1].classification == ipod::deployment::Classification::Podcast);
    assert(manifest.entries[0].control_id == 9000 && manifest.entries[1].control_id == 9001);

    music[5] = 5; // Music must carry runtime flags 0x1.
    auto bad_flags = Deployment({music});
    assert(!ipod::deployment::current::Parse(bad_flags.data(), bad_flags.size(), manifest, error));
    assert(error == Error::Entry);

    auto bad_count = empty;
    bad_count[30] = 0xff;
    bad_count[31] = 0x03;
    assert(!ipod::deployment::current::Parse(bad_count.data(), bad_count.size(), manifest, error));
    assert(error == Error::Count);

    if (argc >= 2) {
        std::ifstream file(argv[1], std::ios::binary);
        assert(file);
        std::vector<std::uint8_t> installed((std::istreambuf_iterator<char>(file)), {});
        assert(ipod::deployment::current::Parse(installed.data(), installed.size(), manifest, error));
        assert(manifest.entries.size() == 3);
        assert(manifest.entries[0].runtime_id.rfind("M4IPOD_", 0) == 0);
        assert(manifest.entries[1].runtime_id.rfind("M4IPOD_", 0) == 0);
        assert(manifest.entries[2].runtime_id.rfind("M4IPOD_", 0) == 0);
        if (argc == 3) {
            for (const auto& entry : manifest.entries) {
                const std::string bank_path = std::string(argv[2]) + "\\" + entry.bank_name;
                const std::string dbm_path = std::string(argv[2]) + "\\" +
                    entry.dbm_path.substr(entry.dbm_path.find_last_of("/") + 1);
                std::ifstream bank_file(bank_path, std::ios::binary);
                std::ifstream dbm_file(dbm_path, std::ios::binary);
                assert(bank_file && dbm_file);
                std::vector<std::uint8_t> bank((std::istreambuf_iterator<char>(bank_file)), {});
                std::vector<std::uint8_t> dbm((std::istreambuf_iterator<char>(dbm_file)), {});
                assert(ipod::deployment::current::ValidateBankGraph(entry, bank));
                assert(ipod::deployment::current::ValidateDbm(entry, dbm));
            }
        }
    }

    std::cout << "native manifest tests passed\n";
}
