#include "../mgs4_ipod_asi.cpp"
#include <cassert>
#include <fstream>
#include <iostream>
#include <iterator>

int main()
{
    wchar_t temporary[MAX_PATH]{};
    wchar_t directory[MAX_PATH]{};
    assert(GetTempPathW(MAX_PATH, directory));
    assert(GetTempFileNameW(directory, L"ipd", 0, temporary));
    wcscpy_s(g_log_path, temporary);
    const std::string message(4096, 'x');
    Log(message.c_str());
    Log("following entry");
    {
        std::ifstream file(temporary, std::ios::binary);
        const std::string contents((std::istreambuf_iterator<char>(file)), {});
        assert(contents.find(message + "\r\n") != std::string::npos);
        assert(contents.find("following entry\r\n") != std::string::npos);
        assert(contents.find('\0') == std::string::npos);
    }
    assert(DeleteFileW(temporary));
    wcscpy_s(g_log_path, directory);
    Log("a directory cannot be opened as a log; this must fail safely");
    std::cout << "native logging tests passed\n";
}
