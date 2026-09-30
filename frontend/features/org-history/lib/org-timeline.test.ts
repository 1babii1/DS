import assert from 'node:assert/strict'
import { test } from 'node:test'

import { buildTree, type ChartDepartment, dateAtOffset, daySpan, headcount, offsetOfDate } from './org-timeline.ts'

const dept = (id: string, parentId: string | null, name: string, people = 0): ChartDepartment => ({
	id,
	parentId,
	name,
	identifier: name.toLowerCase(),
	depth: 0,
	people: Array.from({ length: people }, (_, i) => ({ id: `${id}-${i}`, name: `Person ${i}`, position: 'Developer' }))
})

test('the slider spans from the day of the first event to today', () => {
	assert.equal(daySpan('2026-01-05T09:00:00Z', new Date('2026-01-05T23:00:00Z')), 0)
	assert.equal(daySpan('2026-01-05T09:00:00Z', new Date('2026-01-08T01:00:00Z')), 3)
	assert.equal(daySpan('2026-01-05T23:59:00Z', new Date('2026-01-06T00:01:00Z')), 1)
})

test('with no recorded history, or one that starts in the future, there is nothing to slide over', () => {
	assert.equal(daySpan(null, new Date('2026-01-05T00:00:00Z')), 0)
	assert.equal(daySpan('2027-01-01T00:00:00Z', new Date('2026-01-05T00:00:00Z')), 0)
})

test('a position maps to the bare UTC date it stands for, across month and year ends and leap days', () => {
	assert.equal(dateAtOffset('2026-01-05T09:00:00Z', 0), '2026-01-05')
	assert.equal(dateAtOffset('2026-01-05T09:00:00Z', 27), '2026-02-01')
	assert.equal(dateAtOffset('2027-12-30T20:00:00Z', 3), '2028-01-02')
	assert.equal(dateAtOffset('2028-02-28T00:00:00Z', 1), '2028-02-29')
})

test('a position below zero stays on the first day', () => {
	assert.equal(dateAtOffset('2026-01-05T09:00:00Z', -4), '2026-01-05')
})

test('a date and its position convert back and forth', () => {
	const first = '2026-01-05T09:00:00Z'
	for (const offset of [0, 1, 26, 27, 200]) {
		assert.equal(offsetOfDate(first, dateAtOffset(first, offset)), offset)
	}
})

test('departments nest under their parents in the order they arrived', () => {
	const tree = buildTree([dept('a', null, 'Engineering'), dept('b', null, 'Payments'), dept('c', 'a', 'Platform'), dept('d', 'a', 'Web')])

	assert.deepEqual(tree.map(n => n.name), ['Engineering', 'Payments'])
	assert.deepEqual(tree[0].children.map(n => n.name), ['Platform', 'Web'])
	assert.deepEqual(tree[1].children, [])
})

test('a department whose parent is not in the list is a root, not dropped', () => {
	const tree = buildTree([dept('a', 'missing', 'Orphan'), dept('b', null, 'Payments')])

	assert.deepEqual(tree.map(n => n.name).sort(), ['Orphan', 'Payments'])
})

test('a department that names itself as its parent does not vanish or loop', () => {
	const tree = buildTree([dept('a', 'a', 'Self')])

	assert.deepEqual(tree.map(n => n.name), ['Self'])
	assert.deepEqual(tree[0].children, [])
})

test('the headcount of a department includes everyone below it', () => {
	const [root] = buildTree([dept('a', null, 'Engineering', 2), dept('b', 'a', 'Platform', 3), dept('c', 'b', 'Infra', 1)])

	assert.equal(headcount(root), 6)
	assert.equal(headcount(root.children[0]), 4)
})
