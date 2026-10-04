#include "../programmer_resume.h"
#include <cassert>
#include <iostream>

int main()
{
    using ipod::programmer::IsValidResumePosition;

    assert(!IsValidResumePosition(false, 10357, 300));
    assert(!IsValidResumePosition(true, 0, 300));
    assert(!IsValidResumePosition(true, -1, 300));
    assert(!IsValidResumePosition(true, 300000, 300));
    assert(!IsValidResumePosition(true, 300001, 300));
    assert(IsValidResumePosition(true, 10357, 300));
    assert(IsValidResumePosition(true, 28266, 300));
    assert(ipod::programmer::kFmodTimeunitMilliseconds == 0x00000001);

    std::cout << "programmer resume tests passed\n";
}
