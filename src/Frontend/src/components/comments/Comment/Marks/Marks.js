import React from "react";
import PropTypes from "prop-types";
import { comment, group } from "../../commonPropTypes";
import { EyeClosed, Ok, Pin, People } from "icons";
import { TooltipMenu, MenuItem } from "ui";

import styles from "./Marks.less";

export default function Marks({ courseId, comment, canViewStudentsGroup, authorGroups }) {
	const windowUrl = `${ window.location.origin }/${ courseId }`;
	return (
		<>
			{ !comment.isApproved && <HiddenMark/> }
			{ comment.isCorrectAnswer && <CorrectAnswerMark/> }
			{ comment.isPinnedToTop && <PinnedToTopMark/> }
			{ authorGroups && <GroupMark url={ windowUrl } groups={ authorGroups }/> }
		</>
	)
};

const HiddenMark = () => (
	<div className={ `${ styles.mark } ${ styles.approvedComment }` }>
		<EyeClosed size={ 15 }/>
		<span className={ `${ styles.text } ${ styles.visibleOnDesktopAndTablet }` }>
			Скрыт
		</span>
	</div>
);

const CorrectAnswerMark = () => (
	<div className={ `${ styles.mark } ${ styles.correctAnswer }` }>
		<Ok size={ 15 }/>
		<span className={ `${ styles.text } ${ styles.visibleOnDesktopAndTablet }` }>
			Правильный&nbsp;ответ
		</span>
	</div>
);

const PinnedToTopMark = () => (
	<div className={ `${ styles.mark } ${ styles.pinnedToTop }` }>
		<Pin size={ 15 }/>
		<span className={ `${ styles.text } ${ styles.visibleOnDesktopAndTablet }` }>
			Закреплен
		</span>
	</div>
);

export function GroupMark({ url, groups }) {
	const groupsNumber = groups.length;

	return (
		<>
			<div className={ styles.visibleOnDesktopAndTablet }>
				<div className={ styles.groupList }>
					{ groupsNumber < 3 ?
						groups.map(group =>
							<div key={ group.id }
								 className={ `${ styles.mark } ${ styles.group } ${ group.isArchived && styles.archiveGroup }` }>
								<People size={ 15 }/>
								<a href={ group.apiUrl && `${ url }${ group.apiUrl }` }
								   className={ `${ styles.text } ${ styles.groupName }` }>
									{ group.name }
								</a>
							</div>) :
						<GroupsMenu url={ url } groups={ groups }/> }
				</div>
			</div>
			<div className={ styles.visibleOnPhone }>
				<GroupsMenu url={ url } groups={ groups }/>
			</div>
		</>
	)
};

const GroupsMenu = ({ url, groups }) => (
	<TooltipMenu
		menuWidth="150px"
		positions={ ["bottom right"] }
		caption={
			<div className={ styles.groupMarkOnPhone }>
				<People color="#fff" size={ 15 }/>
				<span className={ `${ styles.text } ${ styles.visibleOnDesktopAndTablet }` }>
					Группы
				</span>
			</div> }>
		{ groups.map(group => !group.isArchived &&
			<MenuItem
				key={ group.id }
				href={ group.apiUrl && `${ url }${ group.apiUrl }` }>
				{ group.name }
			</MenuItem>) }
		{ groups.map(group => group.isArchived &&
			<MenuItem
				key={ group.id }
				href={ group.apiUrl && `${ url }${ group.apiUrl }` }>
				{ group.name }
			</MenuItem>) }
	</TooltipMenu>
);

Marks.propTypes = {
	authorGroups: PropTypes.arrayOf(group),
	courseId: PropTypes.string,
	comment: comment.isRequired,
	canViewStudentsGroup: PropTypes.func,
};
