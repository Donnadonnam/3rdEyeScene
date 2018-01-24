//
// author: Kazys Stepanas
//
#include "3estext2d.h"

using namespace tes;

Text2D::~Text2D()
{
  delete[] _text;
}


bool Text2D::writeCreate(PacketWriter &stream) const
{
  bool ok = true;
  stream.reset(routingId(), CreateMessage::MessageId);
  ok = _data.write(stream) && ok;

  // Write line count and lines.
  const uint16_t textLength = _textLength;
  ok = stream.writeElement(textLength) == sizeof(textLength) && ok;

  if (textLength)
  {
    // Don't write null terminator.
    ok = stream.writeArray(_text, textLength) == sizeof(*_text) * textLength && ok;
  }

  return ok;
}


bool Text2D::readCreate(PacketReader &stream)
{
  if (!Shape::readCreate(stream))
  {
    return false;
  }

  bool ok = true;
  uint16_t textLength = 0;
  ok = ok && stream.readElement(textLength) == sizeof(textLength);

  if (_textLength < textLength)
  {
    delete [] _text;
    _text = new char[textLength + 1];
  }
  _textLength = textLength;

  if (_textLength)
  {
    _text[0] = '\0';
    ok = ok && stream.readArray(_text, textLength) == sizeof(*_text) * textLength;
    _text[textLength] = '\0';
  }

  return ok;
}


Shape *Text2D::clone() const
{
  Text2D *copy = new Text2D(nullptr, (uint16_t)0);
  onClone(copy);
  return copy;
}


void Text2D::onClone(Text2D *copy) const
{
  Shape::onClone(copy);
  copy->setText(_text, _textLength);
}


Text2D &Text2D::setText(const char *text, uint16_t textLength)
{
  delete[] _text;
  _text = nullptr;
  _textLength = 0;
  if (text && textLength)
  {
    _text = new char[textLength + 1];
    _textLength = textLength;
#ifdef _MSC_VER
    strncpy_s(_text, _textLength + 1, text, textLength);
#else  // _MSC_VER
    strncpy(_text, text, textLength);
#endif // _MSC_VER
    _text[textLength] = '\0';
  }
  return *this;
}
