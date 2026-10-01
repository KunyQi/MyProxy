package com.myproxy.android.ui.binding

import androidx.compose.ui.text.AnnotatedString
import androidx.compose.ui.text.input.OffsetMapping
import androidx.compose.ui.text.input.TransformedText
import androidx.compose.ui.text.input.VisualTransformation
import com.myproxy.android.util.PairingCodeNormalizer

/**
 * 把裸码（state 里存的 XXXX…）显示成 XXXX-XXXX。连字符是**纯视觉**的：
 * offset 映射把它排除在编辑坐标之外，自动插入后光标不再错位。
 */
class PairingCodeVisualTransformation : VisualTransformation {

    override fun filter(text: AnnotatedString): TransformedText {
        val cleaned = text.text
        val hasHyphen = cleaned.length > 4
        return TransformedText(
            text = AnnotatedString(PairingCodeNormalizer.display(cleaned)),
            offsetMapping = object : OffsetMapping {
                override fun originalToTransformed(offset: Int): Int =
                    PairingCodeNormalizer.originalToTransformed(offset, hasHyphen)

                override fun transformedToOriginal(offset: Int): Int =
                    PairingCodeNormalizer.transformedToOriginal(offset, hasHyphen)
            }
        )
    }
}
